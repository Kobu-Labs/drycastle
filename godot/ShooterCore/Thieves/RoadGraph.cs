using System.Numerics;
using System.Text.Json;

namespace Drycastle.ShooterCore.Thieves;

/// <summary>A spot on a road edge: the edge and how far along its polyline from its From end, metres.</summary>
public readonly record struct RoadPlace(int Edge, float At);

/// <summary>A road point as a stop candidate: where it is on the ground (x, z) and which edge place it is.</summary>
public readonly record struct RoadPoint(RoadPlace Place, Vector2 Ground);

/// <summary>
/// Where a road crosses the map's edge: the point on the border, the node inside it joins, and the road from the border to that node (the first point is the border point).
/// </summary>
public sealed record RoadEntry(int Edge, Vector2 Border, int InsideNode, Vector2[] Lead);

/// <summary>A polyline on the ground (x, z) with its cumulative lengths: what a van follows. Positions are sampled by distance travelled.</summary>
public sealed class RoadPath
{
    public static readonly RoadPath Empty = new(new List<Vector2>());

    public Vector2[] Points { get; }
    readonly float[] _cum;
    public float Length { get; }

    public RoadPath(IReadOnlyList<Vector2> points)
    {
        var list = new List<Vector2>(points.Count);
        foreach (var p in points)
            if (list.Count == 0 || Vector2.Distance(list[^1], p) > 0.01f) list.Add(p); // no zero-length legs
        Points = list.ToArray();
        _cum = new float[Points.Length];
        for (var i = 1; i < Points.Length; i++) _cum[i] = _cum[i - 1] + Vector2.Distance(Points[i - 1], Points[i]);
        Length = Points.Length > 0 ? _cum[^1] : 0;
    }

    public bool IsEmpty => Points.Length < 2;
    public Vector2 Start => Points[0];
    public Vector2 End => Points[^1];

    /// <summary>The point <paramref name="s"/> metres along the path (clamped to its ends).</summary>
    public Vector2 At(float s)
    {
        if (Points.Length == 0) return Vector2.Zero;
        if (s <= 0 || Points.Length == 1) return Points[0];
        if (s >= Length) return Points[^1];
        var i = Segment(s);
        var len = _cum[i + 1] - _cum[i];
        return Vector2.Lerp(Points[i], Points[i + 1], len > 0 ? (s - _cum[i]) / len : 0);
    }

    /// <summary>The part of the path from <paramref name="from"/> to <paramref name="to"/> metres along (clamped; empty when they are the same point).</summary>
    public RoadPath Slice(float from, float to)
    {
        if (Points.Length == 0) return Empty;
        from = Math.Clamp(from, 0, Length); to = Math.Clamp(to, 0, Length);
        if (to <= from) return Empty;
        var pts = new List<Vector2> { At(from) };
        for (var i = 0; i < Points.Length; i++) if (_cum[i] > from && _cum[i] < to) pts.Add(Points[i]);
        pts.Add(At(to));
        return new RoadPath(pts);
    }

    /// <summary>The same way driven the other way round.</summary>
    public RoadPath Reversed() => new(Points.Reverse().ToArray());

    /// <summary>These paths joined end to start (the second one starts where the first ends, or the leg between is a straight bridge).</summary>
    public RoadPath Then(RoadPath next) => new(Points.Concat(next.Points).ToArray());

    /// <summary>The distance from <paramref name="p"/> to the nearest point of the path.</summary>
    public float DistanceTo(Vector2 p)
    {
        if (Points.Length == 0) return float.PositiveInfinity;
        var best = Vector2.Distance(p, Points[0]);
        for (var i = 0; i + 1 < Points.Length; i++)
        {
            var a = Points[i]; var ab = Points[i + 1] - a;
            var len2 = ab.LengthSquared();
            var t = len2 > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
            best = MathF.Min(best, Vector2.Distance(p, a + ab * t));
        }
        return best;
    }

    /// <summary>How far along the path (metres) its point nearest <paramref name="p"/> lies: where a car that drives it has got to.</summary>
    public float Project(Vector2 p)
    {
        if (Points.Length < 2) return 0;
        var best = float.PositiveInfinity; var bestS = 0f;
        for (var i = 0; i + 1 < Points.Length; i++)
        {
            var a = Points[i]; var ab = Points[i + 1] - a;
            var len2 = ab.LengthSquared();
            var t = len2 > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
            var d = Vector2.DistanceSquared(p, a + ab * t);
            if (d >= best) continue;
            best = d; bestS = _cum[i] + t * MathF.Sqrt(len2);
        }
        return bestS;
    }

    /// <summary>The heading at <paramref name="s"/> as a Godot yaw (0 faces -Z), from a point <paramref name="lookAheadM"/> further along (the last leg's heading at the end).</summary>
    public float YawAt(float s, float lookAheadM = 2f)
    {
        if (Points.Length < 2) return 0;
        var a = At(s); var b = At(s + lookAheadM);
        if (Vector2.DistanceSquared(a, b) < 1e-6f) { a = At(Length - lookAheadM); b = At(Length); }
        return Yaw(b - a);
    }

    /// <summary>Godot yaw of a ground direction (x east, y = z of the world): 0 faces -Z, forward is (-sin yaw, -cos yaw).</summary>
    public static float Yaw(Vector2 d) => MathF.Atan2(-d.X, -d.Y);

    int Segment(float s)
    {
        var lo = 0; var hi = Points.Length - 2;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_cum[mid] <= s) lo = mid; else hi = mid - 1;
        }
        return lo;
    }
}

/// <summary>
/// The road graph of roads.json (GLOSSARY "Raid van", ADR 0021), pure: nodes, edges with their polylines, shortest paths, where roads cross the map edge and stop candidates. Oneway is
/// ignored. The file's axes are map metres (x east, y north); everything here is in world ground axes (x east, <c>Y</c> = the world's z, south: the map's north is -z), as
/// <see cref="GangPlacement"/>. Height is not here: the caller samples the ground.
/// </summary>
public sealed class RoadGraph
{
    sealed class Edge
    {
        public int From, To;
        public Vector2[] Pts = Array.Empty<Vector2>();   // From to To
        public float[] Cum = Array.Empty<float>();
        public float Length;
        public float Width;
    }

    public static readonly RoadGraph Empty = new(new List<Vector2>(), new List<Edge>());

    readonly List<Vector2> _nodes;
    readonly List<Edge> _edges;
    readonly List<(int Edge, int Other)>[] _adj;

    RoadGraph(List<Vector2> nodes, List<Edge> edges)
    {
        _nodes = nodes; _edges = edges;
        _adj = new List<(int, int)>[nodes.Count];
        for (var i = 0; i < nodes.Count; i++) _adj[i] = new List<(int, int)>();
        for (var i = 0; i < edges.Count; i++)
        {
            _adj[edges[i].From].Add((i, edges[i].To));
            _adj[edges[i].To].Add((i, edges[i].From));
        }
    }

    public int NodeCount => _nodes.Count;
    public int EdgeCount => _edges.Count;
    public bool IsEmpty => _edges.Count == 0;
    public Vector2 NodeAt(int node) => _nodes[node];
    public float EdgeLength(int edge) => _edges[edge].Length;

    /// <summary>
    /// Reads roads.json (<c>nodes</c> with id, x, y; <c>edges</c> with from, to, polyline, width). Edges narrower than <paramref name="minWidthM"/> (footpaths), without a polyline or
    /// joining an unknown node are left out. Empty or edge-less input gives <see cref="Empty"/>.
    /// </summary>
    public static RoadGraph Parse(string json, float minWidthM = 0)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("nodes", out var nodesEl) || !root.TryGetProperty("edges", out var edgesEl)) return Empty;
        var nodes = new List<Vector2>();
        var index = new Dictionary<string, int>();
        foreach (var n in nodesEl.EnumerateArray())
        {
            if (!n.TryGetProperty("id", out var id) || id.GetString() is not { } name || !n.TryGetProperty("x", out var x) || !n.TryGetProperty("y", out var y)) continue;
            index[name] = nodes.Count;
            nodes.Add(new Vector2((float)x.GetDouble(), -(float)y.GetDouble()));
        }
        var edges = new List<Edge>();
        foreach (var e in edgesEl.EnumerateArray())
        {
            if (!e.TryGetProperty("from", out var f) || !e.TryGetProperty("to", out var t) || !e.TryGetProperty("polyline", out var line)) continue;
            if (!index.TryGetValue(f.GetString() ?? "", out var from) || !index.TryGetValue(t.GetString() ?? "", out var to)) continue;
            var width = e.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? (float)w.GetDouble() : 4f;
            if (width < minWidthM) continue;
            var pts = new List<Vector2>();
            foreach (var p in line.EnumerateArray())
                if (p.GetArrayLength() >= 2) pts.Add(new Vector2((float)p[0].GetDouble(), -(float)p[1].GetDouble()));
            if (pts.Count < 2) continue;
            edges.Add(MakeEdge(from, to, pts, width));
        }
        return edges.Count == 0 ? Empty : new RoadGraph(nodes, edges);
    }

    /// <summary>A graph from nodes and edges given directly (tests, tools): each edge joins two node indices over a polyline whose ends are those nodes.</summary>
    public static RoadGraph Make(IReadOnlyList<Vector2> nodes, IReadOnlyList<(int From, int To, Vector2[] Polyline)> edges)
    {
        var list = edges.Select(e => MakeEdge(e.From, e.To, e.Polyline, 6f)).ToList();
        return list.Count == 0 ? Empty : new RoadGraph(nodes.ToList(), list);
    }

    static Edge MakeEdge(int from, int to, IReadOnlyList<Vector2> pts, float width)
    {
        var e = new Edge { From = from, To = to, Pts = pts.ToArray(), Width = width };
        e.Cum = new float[e.Pts.Length];
        for (var i = 1; i < e.Pts.Length; i++) e.Cum[i] = e.Cum[i - 1] + Vector2.Distance(e.Pts[i - 1], e.Pts[i]);
        e.Length = e.Cum[^1];
        return e;
    }

    // ---- places ----

    /// <summary>The ground point of a place.</summary>
    public Vector2 PointAt(RoadPlace place)
    {
        var e = _edges[place.Edge];
        return Sub(e, place.At, place.At)[0];
    }

    /// <summary>The heading of the road at a place as a Godot yaw, driving From to To (<paramref name="forward"/>) or To to From.</summary>
    public float YawAt(RoadPlace place, bool forward)
    {
        var e = _edges[place.Edge];
        var a = At(e, MathF.Max(0, place.At - 1f)); var b = At(e, MathF.Min(e.Length, place.At + 1f));
        return RoadPath.Yaw(forward ? b - a : a - b);
    }

    static Vector2 At(Edge e, float s)
    {
        if (s <= 0) return e.Pts[0];
        if (s >= e.Length) return e.Pts[^1];
        var i = Array.BinarySearch(e.Cum, s);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, e.Pts.Length - 2);
        var len = e.Cum[i + 1] - e.Cum[i];
        return Vector2.Lerp(e.Pts[i], e.Pts[i + 1], len > 0 ? (s - e.Cum[i]) / len : 0);
    }

    /// <summary>The part of an edge's polyline from <paramref name="from"/> to <paramref name="to"/> metres along (in either order: the points are returned in that order).</summary>
    static List<Vector2> Sub(Edge e, float from, float to)
    {
        var pts = new List<Vector2> { At(e, from) };
        if (from <= to) { for (var i = 0; i < e.Pts.Length; i++) if (e.Cum[i] > from && e.Cum[i] < to) pts.Add(e.Pts[i]); }
        else { for (var i = e.Pts.Length - 1; i >= 0; i--) if (e.Cum[i] < from && e.Cum[i] > to) pts.Add(e.Pts[i]); }
        if (from != to) pts.Add(At(e, to));
        return pts;
    }

    /// <summary>Stop candidates: a point every <paramref name="stepM"/> along every edge, inside the box <paramref name="min"/> to <paramref name="max"/> shrunk by <paramref name="insetM"/>.</summary>
    public IEnumerable<RoadPoint> PointsAlong(float stepM, Vector2 min, Vector2 max, float insetM)
    {
        for (var i = 0; i < _edges.Count; i++)
        {
            var e = _edges[i];
            var n = Math.Max(1, (int)MathF.Round(e.Length / stepM));
            for (var k = 0; k < n; k++)
            {
                var s = e.Length * (k + 0.5f) / n;
                var p = At(e, s);
                if (p.X < min.X + insetM || p.X > max.X - insetM || p.Y < min.Y + insetM || p.Y > max.Y - insetM) continue;
                yield return new RoadPoint(new RoadPlace(i, s), p);
            }
        }
    }

    /// <summary>The place on the nearest road to <paramref name="p"/> within <paramref name="maxM"/>, with its distance; null when no road is that near.</summary>
    public (RoadPlace Place, float Distance)? Nearest(Vector2 p, float maxM)
    {
        (RoadPlace, float)? best = null;
        var bestD = maxM;
        for (var i = 0; i < _edges.Count; i++)
        {
            var e = _edges[i];
            for (var k = 0; k + 1 < e.Pts.Length; k++)
            {
                var a = e.Pts[k]; var b = e.Pts[k + 1];
                var ab = b - a;
                var len2 = ab.LengthSquared();
                var t = len2 > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
                var d = Vector2.Distance(p, a + ab * t);
                if (d > bestD) continue;
                bestD = d;
                best = (new RoadPlace(i, e.Cum[k] + t * MathF.Sqrt(len2)), d);
            }
        }
        return best;
    }

    // ---- the map edge ----

    /// <summary>
    /// Where roads cross the border of the box <paramref name="min"/> to <paramref name="max"/> (the map's extent) going in: for each edge and each way along it, the last crossing from
    /// outside to inside, when the rest of the road up to its end node stays inside. An edge that dips out again, or has both ends outside, makes no entry.
    /// </summary>
    public List<RoadEntry> EntryPoints(Vector2 min, Vector2 max)
    {
        var entries = new List<RoadEntry>();
        bool In(Vector2 p) => p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y;
        for (var i = 0; i < _edges.Count; i++)
        {
            var e = _edges[i];
            for (var dir = 0; dir < 2; dir++)
            {
                var pts = dir == 0 ? e.Pts : e.Pts.Reverse().ToArray();
                var node = dir == 0 ? e.To : e.From;
                var k = -1;
                for (var j = 0; j + 1 < pts.Length; j++) if (!In(pts[j]) && In(pts[j + 1])) k = j;
                if (k < 0) continue;
                var staysIn = true;
                for (var j = k + 1; j < pts.Length; j++) if (!In(pts[j])) { staysIn = false; break; }
                if (!staysIn) continue;
                var border = Crossing(pts[k], pts[k + 1], min, max);
                var lead = new List<Vector2> { border };
                for (var j = k + 1; j < pts.Length; j++) lead.Add(pts[j]);
                entries.Add(new RoadEntry(i, border, node, lead.ToArray()));
            }
        }
        return entries;
    }

    /// <summary>Where the segment from <paramref name="a"/> (outside) to <paramref name="b"/> (inside) first enters the box.</summary>
    static Vector2 Crossing(Vector2 a, Vector2 b, Vector2 min, Vector2 max)
    {
        var t0 = 0f;
        var d = b - a;
        void Slab(float p, float dp, float lo, float hi)
        {
            if (MathF.Abs(dp) < 1e-9f) return;
            var t1 = (lo - p) / dp; var t2 = (hi - p) / dp;
            t0 = MathF.Max(t0, MathF.Min(t1, t2));
        }
        Slab(a.X, d.X, min.X, max.X);
        Slab(a.Y, d.Y, min.Y, max.Y);
        return a + d * Math.Clamp(t0, 0f, 1f);
    }

    // ---- shortest paths ----

    /// <summary>The length of the shortest path over the edges from the start nodes (each with a head start cost) to every node; infinity where there is none. <c>PrevEdge</c> and <c>PrevNode</c> trace it back (-1 at a start).</summary>
    public sealed class Distances
    {
        public float[] Dist = Array.Empty<float>();
        /// <summary>The cost of a metre of an edge by its width (null: 1), as the distances were worked out.</summary>
        public Func<float, float>? Factor;
        public int[] PrevEdge = Array.Empty<int>();
        public int[] PrevNode = Array.Empty<int>();
        public bool Reaches(int node) => float.IsFinite(Dist[node]);
    }

    /// <summary>The cost of a metre of <paramref name="e"/> under <paramref name="factor"/> (a function of the edge's width; null: 1, the plain length).</summary>
    static float Metre(Edge e, Func<float, float>? factor) => factor?.Invoke(e.Width) ?? 1f;

    /// <summary>The width of the road at a place (roads.json).</summary>
    public float WidthAt(RoadPlace place) => _edges[place.Edge].Width;

    public Distances Dijkstra(IEnumerable<(int Node, float Cost)> starts, int bannedEdge = -1, Func<float, float>? factor = null)
    {
        var d = new Distances { Factor = factor, Dist = new float[_nodes.Count], PrevEdge = new int[_nodes.Count], PrevNode = new int[_nodes.Count] };
        Array.Fill(d.Dist, float.PositiveInfinity); Array.Fill(d.PrevEdge, -1); Array.Fill(d.PrevNode, -1);
        var queue = new PriorityQueue<int, float>();
        foreach (var (node, cost) in starts)
            if (cost < d.Dist[node]) { d.Dist[node] = cost; queue.Enqueue(node, cost); }
        while (queue.TryDequeue(out var u, out var du))
        {
            if (du > d.Dist[u]) continue;
            foreach (var (edge, v) in _adj[u])
            {
                if (edge == bannedEdge) continue;
                var nd = du + _edges[edge].Length * Metre(_edges[edge], factor);
                if (nd >= d.Dist[v]) continue;
                d.Dist[v] = nd; d.PrevEdge[v] = edge; d.PrevNode[v] = u;
                queue.Enqueue(v, nd);
            }
        }
        return d;
    }

    /// <summary>Shortest path between two nodes as its points (both nodes included); null when they are not connected. The one-edge-at-a-time rule of <see cref="Dijkstra"/>: parallel edges give the shorter.</summary>
    public RoadPath? ShortestPath(int fromNode, int toNode)
    {
        var d = Dijkstra(new[] { (fromNode, 0f) });
        if (!d.Reaches(toNode)) return null;
        var pts = new List<Vector2> { _nodes[fromNode] };
        AppendNodePath(pts, d, toNode);
        return new RoadPath(pts);
    }

    /// <summary>The nodes of the traced path are appended as the polylines of its edges, from the start to <paramref name="node"/>. Returns the start node.</summary>
    int AppendNodePath(List<Vector2> pts, Distances d, int node)
    {
        var chain = new List<(int Edge, int To)>();
        var at = node;
        while (d.PrevEdge[at] >= 0) { chain.Add((d.PrevEdge[at], at)); at = d.PrevNode[at]; }
        var start = at;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var (edge, to) = chain[i];
            var e = _edges[edge];
            if (to == e.To) pts.AddRange(e.Pts); else pts.AddRange(e.Pts.Reverse());
        }
        return start;
    }

    // ---- routes between places ----

    /// <summary>
    /// The distances from a place on a road to every node, for a van that stands there: <paramref name="forward"/> true heads towards the edge's To end, false towards From, null either way
    /// for free. Turning round costs <paramref name="uTurnPenaltyM"/> (infinity: never, and the edge it stands on is not driven again).
    /// </summary>
    public Distances DijkstraFrom(RoadPlace from, bool? forward, float uTurnPenaltyM, Func<float, float>? factor = null)
    {
        var e = _edges[from.Edge];
        var m = Metre(e, factor);
        var toCost = (e.Length - from.At) * m; var fromCost = from.At * m;
        if (forward == true) fromCost += uTurnPenaltyM;
        else if (forward == false) toCost += uTurnPenaltyM;
        return Dijkstra(new[] { (e.To, toCost), (e.From, fromCost) }, forward != null && !float.IsFinite(uTurnPenaltyM) ? from.Edge : -1, factor); // no turning back: the edge it stands on is not driven again
    }

    /// <summary>The way from a place to another over the roads (a van at <paramref name="from"/> heading <paramref name="forward"/>, see <see cref="DijkstraFrom"/>) with whether it drives the last edge From to To; null when there is none. <paramref name="d"/> is <see cref="DijkstraFrom"/> of the same arguments.</summary>
    public (RoadPath Path, bool Forward)? RouteBetween(RoadPlace from, bool? forward, float uTurnPenaltyM, RoadPlace to, Distances d) =>
        RouteBetweenCosted(from, forward, uTurnPenaltyM, to, d) is { } r ? (r.Path, r.Forward) : null;

    /// <summary>As <see cref="RouteBetween(RoadPlace, bool?, float, RoadPlace, Distances)"/>, with the route's cost under the distances' factor (its length when they have none).</summary>
    public (RoadPath Path, bool Forward, float Cost)? RouteBetweenCosted(RoadPlace from, bool? forward, float uTurnPenaltyM, RoadPlace to, Distances d)
    {
        var fe = _edges[from.Edge]; var te = _edges[to.Edge];
        var mt = Metre(te, d.Factor);
        var viaFrom = d.Dist[te.From] + to.At * mt;
        var viaTo = d.Dist[te.To] + (te.Length - to.At) * mt;
        var direct = float.PositiveInfinity;
        if (from.Edge == to.Edge)
        {
            var back = (forward == true && to.At < from.At) || (forward == false && to.At > from.At);
            if (back && !float.IsFinite(uTurnPenaltyM)) return null; // a driver that may not turn back does not drive on and turn round at the node either
            direct = MathF.Abs(to.At - from.At) * Metre(fe, d.Factor) + (back ? uTurnPenaltyM : 0f);
        }
        if (!float.IsFinite(direct) && !float.IsFinite(viaFrom) && !float.IsFinite(viaTo)) return null;
        if (direct <= viaFrom && direct <= viaTo) return (new RoadPath(Sub(fe, from.At, to.At)), to.At >= from.At, direct);
        var fwd = viaFrom <= viaTo;
        var end = fwd ? te.From : te.To;
        var pts = new List<Vector2>();
        var start = AppendNodePath(pts, d, end);
        var lead = Sub(fe, from.At, start == fe.From ? 0 : fe.Length);
        pts.InsertRange(0, lead);
        pts.AddRange(fwd ? Sub(te, 0, to.At) : Sub(te, te.Length, to.At));
        return (new RoadPath(pts), fwd, fwd ? viaFrom : viaTo);
    }

    /// <summary>The shortest way between two places, either direction free; null when they are not connected.</summary>
    public (RoadPath Path, bool Forward)? RouteBetween(RoadPlace from, RoadPlace to)
    {
        var d = DijkstraFrom(from, null, 0);
        return RouteBetween(from, null, 0, to, d);
    }

    // ---- routes ----

    /// <summary>
    /// The way in: from the map edge at <paramref name="entry"/> to <paramref name="stop"/>. Returns the path and whether the van drives the stop's edge From to To at the end
    /// (its heading when parked); null when the stop cannot be reached.
    /// </summary>
    public (RoadPath Path, bool Forward)? RouteIn(RoadEntry entry, RoadPlace stop)
    {
        var d = Dijkstra(new[] { (entry.InsideNode, 0f) });
        return RouteIn(entry, stop, d);
    }

    /// <summary>The way in with the distances from the entry's inside node already worked out (<see cref="Dijkstra"/>).</summary>
    public (RoadPath Path, bool Forward)? RouteIn(RoadEntry entry, RoadPlace stop, Distances fromEntry)
    {
        var e = _edges[stop.Edge];
        var viaFrom = fromEntry.Dist[e.From] + stop.At;
        var viaTo = fromEntry.Dist[e.To] + (e.Length - stop.At);
        if (!float.IsFinite(viaFrom) && !float.IsFinite(viaTo)) return null;
        var forward = viaFrom <= viaTo;
        var end = forward ? e.From : e.To;
        var pts = new List<Vector2>(entry.Lead);
        AppendNodePath(pts, fromEntry, end);
        pts.AddRange(forward ? Sub(e, 0, stop.At) : Sub(e, e.Length, stop.At));
        return (new RoadPath(pts), forward);
    }

    /// <summary>
    /// The way out: from <paramref name="from"/>, driving the edge forward or not, over the roads to the nearest of <paramref name="entries"/> and out to the map edge. Driving on is
    /// free, turning round costs <paramref name="uTurnPenaltyM"/> extra. Null when no entry can be reached.
    /// </summary>
    public RoadPath? RouteOut(RoadPlace from, bool forward, IReadOnlyList<RoadEntry> entries, float uTurnPenaltyM)
    {
        if (entries.Count == 0) return null;
        var e = _edges[from.Edge];
        var ahead = forward ? e.To : e.From;
        var behind = forward ? e.From : e.To;
        var aheadCost = forward ? e.Length - from.At : from.At;
        var behindCost = (forward ? from.At : e.Length - from.At) + uTurnPenaltyM;
        var d = Dijkstra(new[] { (ahead, aheadCost), (behind, behindCost) });
        RoadEntry? best = null;
        var bestCost = float.PositiveInfinity;
        foreach (var en in entries)
        {
            var cost = d.Dist[en.InsideNode];
            if (cost < bestCost) { bestCost = cost; best = en; }
        }
        if (best == null || !float.IsFinite(bestCost)) return null;
        var middle = new List<Vector2>();
        var start = AppendNodePath(middle, d, best.InsideNode);
        var pts = new List<Vector2>();
        var viaAhead = start == ahead && (start != behind || aheadCost <= behindCost); // the end it set out for: the one ahead, unless only the one behind was the cheaper start
        pts.AddRange(viaAhead ? Sub(e, from.At, forward ? e.Length : 0) : Sub(e, from.At, forward ? 0 : e.Length));
        pts.AddRange(middle);
        pts.AddRange(best.Lead.Reverse());
        return new RoadPath(pts);
    }
}

/// <summary>Where a Raid's van goes: from the map edge (null for a van that is put at its stop) to the stop, and how it faces there. A plan over a fixed route (<see cref="RaidRoutes"/>) carries its way out (<see cref="Out"/>) and the route's name.</summary>
public sealed record RaidPlan(RoadEntry? Entry, RoadPlace Stop, Vector2 StopAt, bool Forward, float Yaw, RoadPath In, int DoorSide, int Targets, bool Fallback, RoadPath? Out = null, string? RouteName = null);

/// <summary>
/// Picks a Raid's way (GLOSSARY "Raid van"), pure: a random road in from the map edge and the stop: the road point with the most stealable targets within the stop radius that no earlier
/// van of the event stopped near; with no targets anywhere the one nearest to a player. The caller's <c>accept</c> rejects a spot the crew cannot step out on (no navigation mesh there).
/// </summary>
public static class RaidPlanner
{
    /// <summary>A candidate spot as the caller checks it: where, and the heading. Returns the door side (1 right, -1 left) or null to refuse the spot.</summary>
    public delegate int? Accept(Vector2 at, float yaw);

    const int MaxChecks = 60;

    /// <summary>
    /// The plan for one Raid, or null when the roads have no entry point or no stop can be reached (the caller then parks a van near the admin: <see cref="Fallback"/>).
    /// <paramref name="targets"/> are the places something can be stolen (ticket 04 and on feed it; until then the cars), <paramref name="used"/> the stops of earlier vans,
    /// <paramref name="players"/> where the players are.
    /// </summary>
    public static RaidPlan? Plan(RoadGraph g, Vector2 min, Vector2 max, IReadOnlyList<Vector2> targets, IReadOnlyList<Vector2> used, IReadOnlyList<Vector2> players,
        VanConfig cfg, Random rng, Accept accept)
    {
        if (g.IsEmpty) return null;
        var entries = g.EntryPoints(min, max);
        if (entries.Count == 0) return null;
        var order = entries.OrderBy(_ => rng.Next()).ToList();
        var radius2 = cfg.StopRadiusM * cfg.StopRadiusM;
        var apart2 = cfg.UsedApartM * cfg.UsedApartM;
        var scored = new List<(RoadPoint Point, int Score, float Near, int Jitter)>();
        foreach (var rp in g.PointsAlong(cfg.StopStepM, min, max, cfg.StopInsetM))
        {
            var p = rp.Ground;
            if (used.Any(u => Vector2.DistanceSquared(u, p) < apart2)) continue;
            var score = 0;
            foreach (var t in targets) if (Vector2.DistanceSquared(t, p) <= radius2) score++;
            var near = float.PositiveInfinity;
            foreach (var pl in players) near = MathF.Min(near, Vector2.Distance(pl, p));
            scored.Add((rp, score, near, rng.Next()));
        }
        // the most targets first; with none anywhere the nearest to a player; ties at random
        var anyTarget = scored.Any(s => s.Score > 0);
        var ranked = anyTarget
            ? scored.OrderByDescending(s => s.Score).ThenBy(s => s.Jitter)
            : scored.OrderBy(s => s.Near).ThenBy(s => s.Jitter);
        var list = ranked.ToList();
        foreach (var entry in order)
        {
            var d = g.Dijkstra(new[] { (entry.InsideNode, 0f) });
            var checks = 0;
            foreach (var c in list)
            {
                if (checks >= MaxChecks) break;
                if (g.RouteIn(entry, c.Point.Place, d) is not { } route || route.Path.Length < 30f) continue;
                checks++;
                var yaw = route.Path.YawAt(route.Path.Length);
                if (accept(c.Point.Ground, yaw) is not { } side) continue;
                return new RaidPlan(entry, c.Point.Place, c.Point.Ground, route.Forward, yaw, route.Path, side, c.Score, false);
            }
        }
        return null;
    }

    /// <summary>
    /// A van parked on a road near <paramref name="near"/> with no way in: the nearest road spots (at least <paramref name="minM"/> away when there are some) in order until
    /// <paramref name="accept"/> takes one. Null when no road is within reach.
    /// </summary>
    public static RaidPlan? Fallback(RoadGraph g, Vector2 near, float minM, Random rng, Accept accept)
    {
        if (g.IsEmpty) return null;
        var all = new List<(RoadPoint P, float D)>();
        var big = new Vector2(-1e9f, -1e9f); var bigMax = new Vector2(1e9f, 1e9f);
        foreach (var rp in g.PointsAlong(6f, big, bigMax, 0))
        {
            var d = Vector2.Distance(rp.Ground, near);
            if (d <= 400f) all.Add((rp, d));
        }
        var ring = all.Where(a => a.D >= minM).OrderBy(a => a.D + (float)rng.NextDouble() * 20f).Take(MaxChecks);
        foreach (var (p, _) in ring.Concat(all.Where(a => a.D < minM).OrderByDescending(a => a.D).Take(MaxChecks / 3)))
        {
            var yaw = g.YawAt(p.Place, true);
            if (accept(p.Ground, yaw) is not { } side) continue;
            return new RaidPlan(null, p.Place, p.Ground, true, yaw, RoadPath.Empty, side, 0, true);
        }
        return null;
    }

    /// <summary>The way out of a Raid's stop: over the roads to the nearest road that leaves the map, or straight on for <see cref="VanConfig.ExitStraightM"/> when there is none.</summary>
    public static RoadPath Exit(RoadGraph g, RaidPlan plan, Vector2 min, Vector2 max, VanConfig cfg)
    {
        if (g.RouteOut(plan.Stop, plan.Forward, g.EntryPoints(min, max), cfg.UTurnPenaltyM) is { IsEmpty: false } path) return path;
        var fwd = new Vector2(-MathF.Sin(plan.Yaw), -MathF.Cos(plan.Yaw));
        return new RoadPath(new[] { plan.StopAt, plan.StopAt + fwd * cfg.ExitStraightM });
    }
}

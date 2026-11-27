using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// One prop of a dressed Trap house, in Godot axes: kind (the index in trap_house.json <c>props</c>), variant (its model or tag), where it stands (the middle of its footprint on the
/// floor; a graffiti's middle on the wall) and the way its front faces (a Godot yaw, 0 faces -Z, as a player's).
/// </summary>
public readonly record struct TrapProp(byte Kind, byte Variant, Vector3 Pos, float Yaw);

/// <summary>A posted defender's spot (ADR 0046 §3): where they stand (feet), the yaw they face at first (the room's way in), and the room.</summary>
public sealed record DefenderPost(Vector3 Pos, float Yaw, string Room);

/// <summary>The safe: the prop's middle on the floor, the yaw its door faces, where a player stands to work it, and the room.</summary>
public sealed record SafeSpot(Vector3 Pos, float Yaw, Vector3 Stand, string Room);

/// <summary>A spot for a lying Duffel on a table's top: where (its bottom), the table's yaw, and what the Duffel holds (trap_house.json <c>duffel</c>: "product", "cash").</summary>
public sealed record DuffelSpot(Vector3 Pos, float Yaw, string Kind);

/// <summary>What a Trap house is dressed with: the props everyone sees (and the solid ones everyone collides with), and the spots only the server uses (defenders, the safe, the Duffels).</summary>
public sealed record TrapHouseDressing(string House, IReadOnlyList<TrapProp> Props, IReadOnlyList<DefenderPost> Posts, SafeSpot? Safe, IReadOnlyList<DuffelSpot> Duffels);

/// <summary>
/// Dresses a hollow house as a Trap house (#496, ADR 0046), pure: from its floor plan (rooms, room types, doors, links) and a seed, the props of trap_house.json by room type, the safe,
/// the posted defenders' spots and the Duffel spots on the tables. The same house, data and seed give the same dressing. Nothing stands within <c>door_clear_m</c> of a doorway, a
/// door or garage door, or a link between rooms, so every way through the house stays open; props stay inside their room and apart. A prop with no room for it is left out.
/// </summary>
public static class TrapHousePlanner
{
    public static TrapHouseDressing Plan(HouseFloorPlan house, TrapHouseFile data, int seed) => new Run(house, data, seed).Dress();

    /// <summary>The Godot yaw (0 faces -Z, north) of a map local direction (x east, y north).</summary>
    public static float Yaw(Vector2 front) => MathF.Atan2(-front.X, front.Y);

    /// <summary>A prop placed: its footprint (a graffiti's is its line on the wall) and the radius round its centre that holds it, for the cheap tests first.</summary>
    sealed record Placed(int Kind, Vector2 Centre, Vector2 Front, Vector2[] Foot, float Radius, string Room, bool High);

    sealed class Run
    {
        readonly HouseFloorPlan _house;
        readonly TrapHouseFile _data;
        readonly TrapClearanceData _c;
        readonly Random _rng;
        readonly List<(Vector2 A, Vector2 B)> _openings;
        readonly List<Placed> _placed = new();
        readonly Dictionary<string, int> _depth = new();
        readonly Dictionary<string, Vector2> _entrance = new();
        /// <summary>The openings near the room being dressed (within its box and the clearance).</summary>
        List<(Vector2 A, Vector2 B)> _near = new();

        public Run(HouseFloorPlan house, TrapHouseFile data, int seed)
        {
            _house = house; _data = data; _c = data.Clearance;
            _rng = new Random(unchecked((int)Hash(house.Id) ^ seed * 16777619));
            _openings = house.Doors.Select(d => (d.A, d.B)).Concat(house.Rooms.SelectMany(r => r.Links.Select(l => (l.P, l.Q)))).ToList();
            Ways();
        }

        public TrapHouseDressing Dress()
        {
            var safe = Safe();
            foreach (var room in _house.Rooms)
                foreach (var rule in _data.RulesFor(room.Type).OrderBy(r => r.Place)) // the open floor first, then the walls, the graffiti last (clear of what reaches up to it)
                {
                    var n = _rng.Next(rule.Min, rule.Max + 1);
                    for (var i = 0; i < n && Place(room, rule.Kind, rule.Place) is { } p; i++) _placed.Add(p);
                }
            Tables();
            var props = _placed.Select(p =>
            {
                var k = _data.Props[p.Kind];
                return new TrapProp((byte)p.Kind, (byte)_rng.Next(k.Variants), _house.ToGodot(p.Centre, p.High ? k.HeightM : 0), Yaw(p.Front));
            }).ToList();
            var duffels = new List<DuffelSpot>();
            foreach (var p in _placed)
            {
                var k = _data.Props[p.Kind];
                if (k.Duffel == "") continue;
                var right = new Vector2(p.Front.Y, -p.Front.X);
                foreach (var s in k.DuffelSpots) duffels.Add(new DuffelSpot(_house.ToGodot(p.Centre + right * s[0] + p.Front * s[1], k.Up), Yaw(p.Front), k.Duffel));
            }
            return new TrapHouseDressing(_house.Id, props, Posts(), safe, duffels);
        }

        /// <summary>The safe against a wall of the first room type of safe.rooms that has room, the deepest room first; placed before anything else.</summary>
        SafeSpot? Safe()
        {
            var kind = _data.KindOf(_data.Safe.Prop);
            foreach (var room in _data.Safe.Rooms.SelectMany(t => _house.Rooms.Where(r => r.Type == t).OrderByDescending(r => _depth.GetValueOrDefault(r.Id))))
            {
                if (Place(room, kind, TrapPlace.Wall) is not { } s) continue;
                _placed.Add(s);
                var k = _data.Props[kind];
                return new SafeSpot(_house.ToGodot(s.Centre, 0), Yaw(s.Front), _house.ToGodot(s.Centre + s.Front * (k.Depth / 2 + _data.Safe.StandM), 0), room.Id);
            }
            return null;
        }

        /// <summary>A house whose rooms gave fewer tables with Duffel spots than tables.min gets more, largest room first (a wall, else the open floor).</summary>
        void Tables()
        {
            var t = _data.Tables;
            var kinds = t.Props.Select(_data.KindOf).Where(k => k >= 0).ToList();
            if (kinds.Count == 0) return;
            foreach (var room in _house.Rooms.Where(r => !t.SkipTypes.Contains(r.Type)).OrderByDescending(r => r.AreaM2))
            {
                if (_placed.Count(p => _data.Props[p.Kind].Duffel != "") >= t.Min) return;
                var kind = kinds[_rng.Next(kinds.Count)];
                if ((Place(room, kind, TrapPlace.Wall) ?? Place(room, kind, TrapPlace.Centre)) is { } p) _placed.Add(p);
            }
        }

        Placed? Place(PlanRoom room, int kind, TrapPlace place)
        {
            var k = _data.Props[kind];
            Near(room);
            var spots = place == TrapPlace.Centre ? Centre(room, k, kind) : Wall(room, k, kind, place == TrapPlace.WallHigh);
            return spots.Count == 0 ? null : spots[_rng.Next(spots.Count)];
        }

        void Near(PlanRoom room)
        {
            var poly = room.Polygon;
            var m = _c.DoorClearM + _c.TagDoorGapM;
            float x0 = poly.Min(p => p.X) - m, x1 = poly.Max(p => p.X) + m, y0 = poly.Min(p => p.Y) - m, y1 = poly.Max(p => p.Y) + m;
            _near = _openings.Where(o => MathF.Max(o.A.X, o.B.X) >= x0 && MathF.Min(o.A.X, o.B.X) <= x1 && MathF.Max(o.A.Y, o.B.Y) >= y0 && MathF.Min(o.A.Y, o.B.Y) <= y1).ToList();
        }

        bool Free(Vector2 centre, float radius, Vector2[] foot)
        {
            foreach (var (a, b) in _near)
                if (PlanGeometry.PointSegment(centre, a, b) < radius + _c.DoorClearM && PlanGeometry.SegmentPolygon(a, b, foot) < _c.DoorClearM) return false;
            foreach (var p in _placed)
                if (!p.High && Vector2.Distance(centre, p.Centre) < radius + p.Radius + _c.PropGapM && PlanGeometry.PolygonPolygon(foot, p.Foot) < _c.PropGapM) return false;
            return true;
        }

        List<Placed> Wall(PlanRoom room, TrapPropKind k, int kind, bool high)
        {
            var list = new List<Placed>();
            var poly = room.Polygon;
            var ccw = PlanGeometry.SignedArea(poly) > 0;
            var radius = new Vector2(k.Across, k.Depth).Length() / 2;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                var a = poly[j]; var b = poly[i];
                var len = Vector2.Distance(a, b);
                if (len < k.Across + 2 * _c.WallGapM) continue;
                var dir = (b - a) / len;
                var inward = ccw ? new Vector2(-dir.Y, dir.X) : new Vector2(dir.Y, -dir.X);
                for (var t = k.Across / 2 + _c.WallGapM; t <= len - k.Across / 2 - _c.WallGapM + 1e-4f; t += _c.StepM)
                {
                    if (high)
                    {
                        if (Tag(room, k, kind, a + dir * t + inward * _c.WallGapM, dir, inward) is { } tag) list.Add(tag);
                        continue;
                    }
                    var centre = a + dir * t + inward * (_c.WallGapM + k.Depth / 2);
                    var foot = PlanGeometry.Rect(centre, inward, k.Across, k.Depth);
                    if (PlanGeometry.Within(foot, poly) && Free(centre, radius, foot)) list.Add(new Placed(kind, centre, inward, foot, radius, room.Id, false));
                }
            }
            return list;
        }

        /// <summary>A graffiti on the wall at <paramref name="mid"/>: off the openings, apart from the other tags, and clear of a prop that reaches up to it.</summary>
        Placed? Tag(PlanRoom room, TrapPropKind k, int kind, Vector2 mid, Vector2 dir, Vector2 inward)
        {
            Vector2 s0 = mid - dir * (k.Across / 2), s1 = mid + dir * (k.Across / 2);
            if (!PlanGeometry.Inside(room.Polygon, mid) || _near.Any(o => PlanGeometry.SegmentSegment(s0, s1, o.A, o.B) < _c.TagDoorGapM)) return null;
            var bottom = k.HeightM - k.Up / 2;
            foreach (var p in _placed)
                if (p.High ? Vector2.Distance(p.Centre, mid) < _c.TagApartM : _data.Props[p.Kind].Up > bottom && PlanGeometry.SegmentPolygon(s0, s1, p.Foot) < _c.WallGapM) return null;
            return new Placed(kind, mid, inward, new[] { s0, s1 }, k.Across / 2, room.Id, true);
        }

        List<Placed> Centre(PlanRoom room, TrapPropKind k, int kind)
        {
            var list = new List<Placed>();
            var poly = room.Polygon;
            var (a, b) = Enumerable.Range(0, poly.Length).Select(i => (poly[i], poly[(i + 1) % poly.Length])).MaxBy(e => Vector2.DistanceSquared(e.Item1, e.Item2));
            var along = Vector2.Normalize(b - a);
            var fronts = new[] { along, new Vector2(-along.Y, along.X) };
            var radius = new Vector2(k.Across, k.Depth).Length() / 2;
            var halfMin = MathF.Min(k.Across, k.Depth) / 2;
            float x0 = poly.Min(p => p.X), x1 = poly.Max(p => p.X), y0 = poly.Min(p => p.Y), y1 = poly.Max(p => p.Y);
            for (var x = x0; x <= x1; x += _c.StepM)
                for (var y = y0; y <= y1; y += _c.StepM)
                {
                    var centre = new Vector2(x, y);
                    if (!PlanGeometry.Inside(poly, centre) || PlanGeometry.ToOutline(poly, centre) < _c.CentreWallGapM + halfMin) continue; // cannot keep the way round
                    foreach (var front in fronts)
                    {
                        var foot = PlanGeometry.Rect(centre, front, k.Across, k.Depth);
                        if (PlanGeometry.Within(foot, poly) && PlanGeometry.AreaToOutline(foot, poly) >= _c.CentreWallGapM && Free(centre, radius, foot))
                            list.Add(new Placed(kind, centre, front, foot, radius, room.Id, false));
                    }
                }
            return list;
        }

        /// <summary>Posted defenders' spots, round-robin over the rooms: the first of every room, then the second.</summary>
        List<DefenderPost> Posts()
        {
            var q = _data.Posts;
            var perRoom = new List<List<DefenderPost>>();
            foreach (var room in _house.Rooms)
            {
                if (q.PerRoom == 0 || room.AreaM2 < q.MinRoomM2 || q.SkipTypes.Contains(room.Type) || !_entrance.TryGetValue(room.Id, out var way)) continue;
                Near(room);
                var poly = room.Polygon;
                var spots = new List<(Vector2 P, float Score)>();
                float x0 = poly.Min(p => p.X), x1 = poly.Max(p => p.X), y0 = poly.Min(p => p.Y), y1 = poly.Max(p => p.Y);
                for (var x = x0; x <= x1; x += _c.StepM)
                    for (var y = y0; y <= y1; y += _c.StepM)
                    {
                        var p = new Vector2(x, y);
                        if (!PlanGeometry.Inside(poly, p) || PlanGeometry.ToOutline(poly, p) < q.WallGapM) continue;
                        if (_near.Any(o => PlanGeometry.PointSegment(p, o.A, o.B) < _c.DoorClearM)) continue;
                        if (_placed.Any(f => !f.High && Vector2.Distance(p, f.Centre) < f.Radius + q.PropGapM && (PlanGeometry.Inside(f.Foot, p) || PlanGeometry.ToOutline(f.Foot, p) < q.PropGapM))) continue;
                        spots.Add((p, Vector2.Distance(p, way) + _rng.NextSingle() * _c.StepM));
                    }
                var mine = new List<DefenderPost>();
                var taken = new List<Vector2>();
                foreach (var (p, _) in spots.OrderByDescending(s => s.Score))
                {
                    if (mine.Count >= q.PerRoom) break;
                    if (taken.Any(t => Vector2.Distance(t, p) < q.ApartM)) continue;
                    taken.Add(p);
                    var look = way - p;
                    mine.Add(new DefenderPost(_house.ToGodot(p, 0), look.LengthSquared() > 1e-6f ? Yaw(Vector2.Normalize(look)) : 0, room.Id));
                }
                if (mine.Count > 0) perRoom.Add(mine);
            }
            var all = new List<DefenderPost>();
            for (var i = 0; perRoom.Any(r => r.Count > i); i++) all.AddRange(perRoom.Where(r => r.Count > i).Select(r => r[i]));
            return all;
        }

        /// <summary>
        /// Each room's depth (links from the room the front door opens into) and its way in: the middle of the link it is reached by, or, for the front room, of its front door (else
        /// its garage door). A room no way reaches has neither.
        /// </summary>
        void Ways()
        {
            var doors = _house.Doors.ToDictionary(d => d.Id);
            var queue = new Queue<PlanRoom>();
            foreach (var r in _house.Rooms)
            {
                var door = r.Doors.Concat(r.GarageDoors).Select(id => doors.GetValueOrDefault(id)).FirstOrDefault(d => d != null);
                if (door == null) continue;
                _depth[r.Id] = 0; _entrance[r.Id] = (door.A + door.B) / 2;
                queue.Enqueue(r);
            }
            while (queue.Count > 0)
            {
                var r = queue.Dequeue();
                foreach (var l in r.Links)
                {
                    if (_depth.ContainsKey(l.To) || _house.Room(l.To) is not { } next) continue;
                    _depth[l.To] = _depth[r.Id] + 1; _entrance[l.To] = (l.P + l.Q) / 2;
                    queue.Enqueue(next);
                }
            }
        }
    }

    /// <summary>32-bit FNV-1a of a house id: the same on every process (string.GetHashCode is not).</summary>
    static uint Hash(string s)
    {
        var h = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 16777619u; }
        return h;
    }
}

/// <summary>
/// The Trap house events: <see cref="Net.EventKind.TrapHouseDressed"/> (u8 id length, the house id in UTF-8, u16 prop count, then per prop u8 kind, u8 variant, f32 x, y, z (Godot)
/// and u16 yaw (a turn in 65536 steps), all little endian) and <see cref="Net.EventKind.TrapHouseStripped"/> (u8 id length, the house id).
/// </summary>
public static class TrapHouseCodec
{
    const int PropBytes = 16;

    public static byte[] EncodeDressed(string house, IReadOnlyList<TrapProp> props)
    {
        var id = Id(house);
        var o = new byte[1 + id.Length + 2 + props.Count * PropBytes];
        o[0] = (byte)id.Length;
        id.CopyTo(o, 1);
        var at = 1 + id.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(at), (ushort)props.Count);
        at += 2;
        foreach (var p in props)
        {
            o[at] = p.Kind; o[at + 1] = p.Variant;
            BinaryPrimitives.WriteSingleLittleEndian(o.AsSpan(at + 2), p.Pos.X);
            BinaryPrimitives.WriteSingleLittleEndian(o.AsSpan(at + 6), p.Pos.Y);
            BinaryPrimitives.WriteSingleLittleEndian(o.AsSpan(at + 10), p.Pos.Z);
            var turn = p.Yaw / (2 * MathF.PI);
            BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(at + 14), (ushort)((int)MathF.Round((turn - MathF.Floor(turn)) * 65536) & 0xffff));
            at += PropBytes;
        }
        return o;
    }

    public static (string House, TrapProp[] Props)? DecodeDressed(byte[] payload)
    {
        if (payload.Length < 1 || payload.Length < 1 + payload[0] + 2) return null;
        var house = Encoding.UTF8.GetString(payload, 1, payload[0]);
        var at = 1 + payload[0];
        var n = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(at));
        at += 2;
        if (payload.Length != at + n * PropBytes) return null;
        var props = new TrapProp[n];
        for (var i = 0; i < n; i++, at += PropBytes)
            props[i] = new TrapProp(payload[at], payload[at + 1],
                new Vector3(BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(at + 2)), BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(at + 6)), BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(at + 10))),
                BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(at + 14)) / 65536f * 2 * MathF.PI);
        return (house, props);
    }

    public static byte[] EncodeStripped(string house)
    {
        var id = Id(house);
        var o = new byte[1 + id.Length];
        o[0] = (byte)id.Length;
        id.CopyTo(o, 1);
        return o;
    }

    public static string? DecodeStripped(byte[] payload) =>
        payload.Length >= 1 && payload.Length == 1 + payload[0] ? Encoding.UTF8.GetString(payload, 1, payload[0]) : null;

    static byte[] Id(string house)
    {
        var id = Encoding.UTF8.GetBytes(house);
        if (id.Length > 255) throw new ArgumentException($"house id '{house}' is longer than 255 bytes");
        return id;
    }
}

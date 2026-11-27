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
/// the posted defenders' spots (<see cref="DefenderPosts"/>) and the Duffel spots on the tables. The same house, data and seed give the same dressing. Nothing stands within <c>door_clear_m</c> of a doorway, a
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
        readonly IReadOnlyDictionary<string, int> _depth;
        /// <summary>The openings near the room being dressed (within its box and the clearance).</summary>
        List<(Vector2 A, Vector2 B)> _near = new();

        public Run(HouseFloorPlan house, TrapHouseFile data, int seed)
        {
            _house = house; _data = data; _c = data.Clearance;
            _rng = new Random(unchecked((int)Hash(house.Id) ^ seed * 16777619));
            _openings = Openings(house);
            _depth = DefenderPosts.Ways(house).Depth;
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
            var footprints = _placed.Where(p => !p.High).Select(p => new PropFootprint(p.Centre, p.Radius, p.Foot)).ToList();
            return new TrapHouseDressing(_house.Id, props, DefenderPosts.Plan(_house, footprints, _data, _rng), safe, duffels);
        }

        /// <summary>
        /// The safe against a wall of the first room type of safe.rooms that has room, the deepest room first; placed before anything else. Its footprint for the rest of the dressing
        /// is the floor its open door pays out on too (<see cref="SafeKeepOut"/>), so the cash Duffels never lie in a wall or under a prop.
        /// </summary>
        SafeSpot? Safe()
        {
            var kind = _data.KindOf(_data.Safe.Prop);
            var keepOut = SafeKeepOut(_data);
            foreach (var room in _data.Safe.Rooms.SelectMany(t => _house.Rooms.Where(r => r.Type == t).OrderByDescending(r => _depth.GetValueOrDefault(r.Id))))
            {
                Near(room);
                var spots = Wall(room, _data.Props[kind], kind, false, keepOut);
                if (spots.Count == 0) continue;
                var s = spots[_rng.Next(spots.Count)];
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

        void Near(PlanRoom room) => _near = NearOpenings(_openings, room.Polygon, _c.DoorClearM + _c.TagDoorGapM);

        bool Free(Vector2 centre, float radius, Vector2[] foot)
        {
            foreach (var (a, b) in _near)
                if (PlanGeometry.PointSegment(centre, a, b) < radius + _c.DoorClearM && PlanGeometry.SegmentPolygon(a, b, foot) < _c.DoorClearM) return false;
            foreach (var p in _placed)
                if (!p.High && Vector2.Distance(centre, p.Centre) < radius + p.Radius + _c.PropGapM && PlanGeometry.PolygonPolygon(foot, p.Foot) < _c.PropGapM) return false;
            return true;
        }

        /// <param name="keepOut">
        /// The floor the prop holds, when more than its own footprint (across, depth from the wall; the prop stands at its back, in the middle): it must lie in the room and free, and stays
        /// the prop's footprint for what comes after.
        /// </param>
        List<Placed> Wall(PlanRoom room, TrapPropKind k, int kind, bool high, Vector2? keepOut = null)
        {
            var list = new List<Placed>();
            var poly = room.Polygon;
            var ccw = PlanGeometry.SignedArea(poly) > 0;
            var (across, depth) = keepOut is { } ko ? (MathF.Max(ko.X, k.Across), MathF.Max(ko.Y, k.Depth)) : (k.Across, k.Depth);
            var radius = new Vector2(across / 2, MathF.Max(k.Depth / 2, depth - k.Depth / 2)).Length(); // from the prop's centre to the far corners of what it holds
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                var a = poly[j]; var b = poly[i];
                var len = Vector2.Distance(a, b);
                if (len < across + 2 * _c.WallGapM) continue;
                var dir = (b - a) / len;
                var inward = ccw ? new Vector2(-dir.Y, dir.X) : new Vector2(dir.Y, -dir.X);
                for (var t = across / 2 + _c.WallGapM; t <= len - across / 2 - _c.WallGapM + 1e-4f; t += _c.StepM)
                {
                    if (high)
                    {
                        if (Tag(room, k, kind, a + dir * t + inward * _c.WallGapM, dir, inward) is { } tag) list.Add(tag);
                        continue;
                    }
                    var centre = a + dir * t + inward * (_c.WallGapM + k.Depth / 2);
                    var foot = PlanGeometry.Rect(a + dir * t + inward * (_c.WallGapM + depth / 2), inward, across, depth);
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
            foreach (var centre in GridSpots(poly, _c.StepM, _c.CentreWallGapM + halfMin)) // nearer the wall cannot keep the way round
                foreach (var front in fronts)
                {
                    var foot = PlanGeometry.Rect(centre, front, k.Across, k.Depth);
                    if (PlanGeometry.Within(foot, poly) && PlanGeometry.AreaToOutline(foot, poly) >= _c.CentreWallGapM && Free(centre, radius, foot))
                        list.Add(new Placed(kind, centre, front, foot, radius, room.Id, false));
                }
            return list;
        }
    }

    /// <summary>The safe's keep-out on the floor (across, depth from the wall): the widest row of cash Duffels any tier pays out (<see cref="DrillRules.CashSpots"/>), a spread for each, out to as far past them as they lie off its face.</summary>
    public static Vector2 SafeKeepOut(TrapHouseFile data)
    {
        var k = data.Props[data.KindOf(data.Safe.Prop)];
        var d = data.Drill;
        return new Vector2(MathF.Max(k.Across, d.CashDuffels.Max() * d.DuffelSpreadM), k.Depth + 2 * d.DuffelOutM);
    }

    /// <summary>Every way through a house: its doors and the links between its rooms.</summary>
    internal static List<(Vector2 A, Vector2 B)> Openings(HouseFloorPlan house) =>
        house.Doors.Select(d => (d.A, d.B)).Concat(house.Rooms.SelectMany(r => r.Links.Select(l => (l.P, l.Q)))).ToList();

    /// <summary>The openings that reach within <paramref name="margin"/> of <paramref name="poly"/>'s box: the only ones a spot in it can come near.</summary>
    internal static List<(Vector2 A, Vector2 B)> NearOpenings(List<(Vector2 A, Vector2 B)> openings, Vector2[] poly, float margin)
    {
        float x0 = poly.Min(p => p.X) - margin, x1 = poly.Max(p => p.X) + margin, y0 = poly.Min(p => p.Y) - margin, y1 = poly.Max(p => p.Y) + margin;
        return openings.Where(o => MathF.Max(o.A.X, o.B.X) >= x0 && MathF.Min(o.A.X, o.B.X) <= x1 && MathF.Max(o.A.Y, o.B.Y) >= y0 && MathF.Min(o.A.Y, o.B.Y) <= y1).ToList();
    }

    /// <summary>The points of a <paramref name="step"/> grid over <paramref name="poly"/>'s box that lie inside it, at least <paramref name="wallGap"/> from its outline; x outer, y inner.</summary>
    internal static IEnumerable<Vector2> GridSpots(Vector2[] poly, float step, float wallGap)
    {
        float x0 = poly.Min(p => p.X), x1 = poly.Max(p => p.X), y0 = poly.Min(p => p.Y), y1 = poly.Max(p => p.Y);
        for (var x = x0; x <= x1; x += step)
            for (var y = y0; y <= y1; y += step)
            {
                var p = new Vector2(x, y);
                if (PlanGeometry.Inside(poly, p) && PlanGeometry.ToOutline(poly, p) >= wallGap) yield return p;
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
/// The Trap house events: <see cref="Net.EventKind.TrapHouseDressed"/> (the house id (<see cref="HouseIdWire"/>: u8 length, UTF-8), u16 prop count, then per prop u8 kind, u8 variant, f32 x, y, z (Godot)
/// and u16 yaw (a turn in 65536 steps), all little endian) and <see cref="Net.EventKind.TrapHouseStripped"/> (the house id alone).
/// </summary>
public static class TrapHouseCodec
{
    const int PropBytes = 16;

    public static byte[] EncodeDressed(string house, IReadOnlyList<TrapProp> props)
    {
        var id = HouseIdWire.Bytes(house);
        var o = new byte[1 + id.Length + 2 + props.Count * PropBytes];
        var at = HouseIdWire.Write(o, 0, id);
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
        if (HouseIdWire.Read(payload, 0, out var at) is not { } house || payload.Length < at + 2) return null;
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

    public static byte[] EncodeStripped(string house) => HouseIdWire.Encode(house);

    public static string? DecodeStripped(byte[] payload) => HouseIdWire.Decode(payload);
}

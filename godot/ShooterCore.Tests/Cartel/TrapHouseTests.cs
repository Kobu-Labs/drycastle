using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>The Trap house dressing (#496, ADR 0046): trap_house.json, the planner's rules (ways clear, props in their rooms and apart, safe, posts, Duffel spots), the wire and the house choice.</summary>
public class TrapHouseTests
{
    static readonly TrapHouseFile Data = TrapHouseFile.Load(Path.Combine(AppContext.BaseDirectory, "data", TrapHouseFile.FileName));

    /// <summary>
    /// A 12 x 8 m house at the origin (map local, floor at z 100), its front door in the south wall at x 6: a hall strip x 5..7 the full depth, a living room west of it (door at
    /// y 2), a bedroom east (y 2..8, door at y 4) and a storage room east (y 0..2, door at y 1).
    /// </summary>
    const string Sample = """
        {
          "version": 1,
          "houses": [ {
            "id": "w1", "parcel": "P-1", "floor_z": 100, "ceiling_z": 102.6,
            "room": { "polygon": [[0, 0], [12, 0], [12, 8], [0, 8]], "z0": 99.8, "z1": 102.7 },
            "doors": [ { "id": "d1", "kind": "hinged", "pos": [6, 0, 100], "yaw": 180, "width": 1.0, "height": 2.1 } ],
            "rooms": [
              { "id": "r0", "kind": "hall", "type": "hall", "area_m2": 16, "polygon": [[5, 0], [7, 0], [7, 8], [5, 8]], "doors": ["d1"], "garage_doors": [],
                "links": [ { "to": "r1", "kind": "door", "p": [5, 1.55], "q": [5, 2.45] }, { "to": "r2", "kind": "door", "p": [7, 3.55], "q": [7, 4.45] }, { "to": "r3", "kind": "door", "p": [7, 0.55], "q": [7, 1.45] } ] },
              { "id": "r1", "kind": "room", "type": "living", "area_m2": 40, "polygon": [[0, 0], [5, 0], [5, 8], [0, 8]], "doors": [], "garage_doors": [],
                "links": [ { "to": "r0", "kind": "door", "p": [5, 1.55], "q": [5, 2.45] } ] },
              { "id": "r2", "kind": "room", "type": "bedroom", "area_m2": 30, "polygon": [[7, 2], [12, 2], [12, 8], [7, 8]], "doors": [], "garage_doors": [],
                "links": [ { "to": "r0", "kind": "door", "p": [7, 3.55], "q": [7, 4.45] } ] },
              { "id": "r3", "kind": "room", "type": "storage", "area_m2": 10, "polygon": [[7, 0], [12, 0], [12, 2], [7, 2]], "doors": [], "garage_doors": [],
                "links": [ { "to": "r0", "kind": "door", "p": [7, 0.55], "q": [7, 1.45] } ] }
            ] } ]
        }
        """;

    static HouseFloorPlan House => HouseFloorPlan.Parse(Sample).Single();

    static Vector2 Local(Vector3 godot) => new(godot.X, -godot.Z);
    static Vector2 Front(float yaw) => new(-MathF.Sin(yaw), MathF.Cos(yaw));

    static Vector2[] Foot(TrapProp p)
    {
        var k = Data.Props[p.Kind];
        return PlanGeometry.Rect(Local(p.Pos), Front(p.Yaw), k.Across, k.Depth);
    }

    /// <summary>Every rule the dressing of <paramref name="house"/> must keep, whatever the seed.</summary>
    static void Keeps(HouseFloorPlan house, TrapHouseDressing d)
    {
        var c = Data.Clearance;
        var openings = house.Doors.Select(x => (x.A, x.B)).Concat(house.Rooms.SelectMany(r => r.Links.Select(l => (l.P, l.Q)))).ToList();
        var floor = d.Props.Where(p => Data.Props[p.Kind].Tags.Count == 0).ToList();
        foreach (var p in floor)
        {
            var foot = Foot(p);
            Assert.Contains(house.Rooms, r => PlanGeometry.Within(foot, r.Polygon)); // inside one room
            Assert.All(openings, o => Assert.True(PlanGeometry.SegmentPolygon(o.Item1, o.Item2, foot) >= c.DoorClearM - 1e-3f, $"{Data.Props[p.Kind].Id} blocks a way in {house.Id}"));
            Assert.Equal(house.FloorZ, p.Pos.Y, 3);
        }
        for (var i = 0; i < floor.Count; i++)
            for (var j = i + 1; j < floor.Count; j++)
                Assert.True(PlanGeometry.PolygonPolygon(Foot(floor[i]), Foot(floor[j])) >= c.PropGapM - 1e-3f, $"two props touch in {house.Id}");
        foreach (var post in d.Posts)
        {
            var p = Local(post.Pos);
            Assert.All(openings, o => Assert.True(PlanGeometry.PointSegment(p, o.Item1, o.Item2) >= c.DoorClearM - 1e-3f));
            Assert.All(floor, f => Assert.False(PlanGeometry.Inside(Foot(f), p)));
            Assert.True(PlanGeometry.Inside(house.Room(post.Room)!.Polygon, p));
        }
        Assert.Equal(d.Props.Count(p => Data.Props[p.Kind].Duffel != "") * 2, d.Duffels.Count);
        if (d.Safe is { } safe) // the open safe's cash, at every tier: on its room's floor, never in a wall or a solid prop
            foreach (var n in Data.Drill.CashDuffels.Distinct())
                Assert.All(DrillRules.CashSpots(Data.Drill, safe, Data.Props[Data.KindOf("safe")].Depth, n), s =>
                {
                    Assert.True(PlanGeometry.Inside(house.Room(safe.Room)!.Polygon, Local(s)), $"a cash duffel of {house.Id} lies outside the safe's room");
                    Assert.All(floor.Where(p => Data.Props[p.Kind].Solid), p => Assert.False(PlanGeometry.Inside(Foot(p), Local(s)), $"a cash duffel of {house.Id} lies in a {Data.Props[p.Kind].Id}"));
                });
    }

    [Fact]
    public void The_shipped_file_reads()
    {
        Assert.True(Data.KindOf("safe") >= 0);
        Assert.NotEmpty(Data.RulesFor("living"));
        Assert.Empty(Data.RulesFor("no_such_type"));
        Assert.All(Data.Props.Where(p => p.Solid), p => Assert.True(p.Depth > 0));
    }

    [Fact]
    public void A_file_with_a_room_row_naming_no_prop_is_refused()
    {
        var bad = """{ "version": 1, "safe": { "prop": "safe" }, "props": [ { "id": "safe", "solid": true, "size": [0.5, 0.6, 0.6], "models": ["m"] } ], "rooms": { "hall": [ ["sofa", 1, 1, "wall"] ] } }""";
        var e = Assert.Throws<DataFileException>(() => TrapHouseFile.Parse(bad));
        Assert.Contains("sofa", e.Message);
    }

    [Fact]
    public void The_floor_plan_reads_rooms_doors_and_links()
    {
        var h = House;
        Assert.Equal(4, h.Rooms.Length);
        Assert.Equal("living", h.Room("r1")!.Type);
        var d = h.Doors.Single();
        Assert.True(Vector2.Distance((d.A + d.B) / 2, new Vector2(6, 0)) < 1e-4f);
        Assert.Equal(1f, Vector2.Distance(d.A, d.B), 3); // across the south wall
        Assert.Equal(0f, d.A.Y, 3);
        Assert.True(h.Contains(new Vector3(3, 100, -3)));
        Assert.False(h.Contains(new Vector3(3, 100, 3))); // south of the house
        Assert.False(h.Contains(new Vector3(3, 104, -3))); // on the roof
    }

    [Fact]
    public void A_dressing_keeps_the_ways_clear_and_its_props_apart_for_any_seed()
    {
        for (var seed = 0; seed < 40; seed++) Keeps(House, TrapHousePlanner.Plan(House, Data, seed));
    }

    [Fact]
    public void The_same_seed_gives_the_same_dressing()
    {
        var a = TrapHousePlanner.Plan(House, Data, 7);
        var b = TrapHousePlanner.Plan(House, Data, 7);
        Assert.Equal(a.Props, b.Props);
        Assert.Equal(a.Posts, b.Posts);
        Assert.Equal(a.Safe, b.Safe);
        Assert.Contains(Enumerable.Range(0, 20), s => !TrapHousePlanner.Plan(House, Data, s).Props.SequenceEqual(a.Props));
    }

    [Fact]
    public void The_living_room_gets_a_table_with_duffel_spots_and_the_bedroom_the_safe()
    {
        var d = TrapHousePlanner.Plan(House, Data, 3);
        var table = Data.KindOf("table_product");
        Assert.Contains(d.Props, p => p.Kind == table && PlanGeometry.Inside(House.Room("r1")!.Polygon, Local(p.Pos)));
        Assert.Contains(d.Duffels, s => s.Kind == "product" && Math.Abs(s.Pos.Y - (100 + Data.Props[table].Up)) < 1e-3f);
        Assert.NotNull(d.Safe);
        Assert.Equal("r2", d.Safe!.Room); // bedroom first in safe.rooms
        Assert.Contains(d.Props, p => p.Kind == Data.KindOf("safe"));
        Assert.True(PlanGeometry.Inside(House.Room("r2")!.Polygon, Local(d.Safe.Stand)));
    }

    [Fact]
    public void Posts_spread_over_the_rooms_and_face_their_way_in()
    {
        var d = TrapHousePlanner.Plan(House, Data, 5);
        var rooms = d.Posts.Select(p => p.Room).ToList();
        var distinct = rooms.Distinct().Count();
        Assert.True(distinct >= 3);
        Assert.Equal(rooms.Take(distinct).Distinct().Count(), distinct); // round-robin: the first of every room first
        var post = d.Posts.First(p => p.Room == "r1");
        var toDoor = Vector2.Normalize(new Vector2(5, 2) - Local(post.Pos));
        Assert.True(Vector2.Dot(Front(post.Yaw), toDoor) > 0.99f);
    }

    [Fact]
    public void Defender_posts_keep_off_the_props_and_the_doorways()
    {
        var q = Data.Posts;
        var none = DefenderPosts.Plan(House, Array.Empty<PropFootprint>(), Data, new Random(1));
        Assert.Equal(none.Select(p => p.Room).Distinct().Count(), none.Select(p => p.Room).Take(none.Select(p => p.Room).Distinct().Count()).Distinct().Count()); // round-robin
        var first = none.First(p => p.Room == "r1");
        var block = PlanGeometry.Rect(Local(first.Pos), new Vector2(0, 1), 1, 1); // a prop where the living room's first post stood
        var posts = DefenderPosts.Plan(House, new[] { new PropFootprint(Local(first.Pos), 0.71f, block) }, Data, new Random(1));
        Assert.Contains(posts, p => p.Room == "r1");
        Assert.All(posts, p =>
        {
            var at = Local(p.Pos);
            Assert.False(PlanGeometry.Inside(block, at));
            Assert.True(PlanGeometry.ToOutline(block, at) >= q.PropGapM - 1e-3f);
            Assert.All(House.Doors, d => Assert.True(PlanGeometry.PointSegment(at, d.A, d.B) >= Data.Clearance.DoorClearM - 1e-3f));
        });
        var (depth, entrance) = DefenderPosts.Ways(House);
        Assert.Equal(0, depth["r0"]); // the front door's room
        Assert.Equal(1, depth["r2"]);
        Assert.True(Vector2.Distance(entrance["r2"], new Vector2(7, 4)) < 1e-4f); // reached by its link
    }

    [Fact]
    public void The_dressing_goes_over_the_wire()
    {
        var d = TrapHousePlanner.Plan(House, Data, 1);
        var back = TrapHouseCodec.DecodeDressed(TrapHouseCodec.EncodeDressed(d.House, d.Props))!.Value;
        Assert.Equal("w1", back.House);
        Assert.Equal(d.Props.Count, back.Props.Length);
        for (var i = 0; i < back.Props.Length; i++)
        {
            Assert.Equal(d.Props[i].Kind, back.Props[i].Kind);
            Assert.Equal(d.Props[i].Variant, back.Props[i].Variant);
            Assert.Equal(d.Props[i].Pos, back.Props[i].Pos);
            Assert.True(Vector2.Dot(Front(d.Props[i].Yaw), Front(back.Props[i].Yaw)) > 0.9999f);
        }
        Assert.Equal("w1", TrapHouseCodec.DecodeStripped(TrapHouseCodec.EncodeStripped("w1")));
        Assert.Null(TrapHouseCodec.DecodeDressed(new byte[] { 2, 0x77 }));
        Assert.Null(TrapHouseCodec.DecodeStripped(new byte[] { 5, 0x77 }));
        Assert.Equal(new byte[] { 2, 0x77, 0x31 }, TrapHouseCodec.EncodeStripped("w1")); // one house id format: u8 length, UTF-8
        Assert.Equal(TrapHouseCodec.EncodeStripped("w1"), DrillCodec.EncodeCleared("w1"));
        Assert.Equal(new byte[] { 2, 0x77, 0x31, 0, 0 }, TrapHouseCodec.EncodeDressed("w1", Array.Empty<TrapProp>()));
        Assert.Equal(66, (int)EventKind.TrapHouseDressed);
        Assert.Equal(67, (int)EventKind.TrapHouseStripped);
    }

    [Fact]
    public void A_job_takes_a_house_near_a_road_no_job_uses_and_nobody_is_in()
    {
        var houses = new[] { House };
        // a road along y = -10 (map local; the graph is in Godot ground axes, x and z = -y)
        var near = RoadGraph.Make(new[] { new Vector2(-50, 10), new Vector2(50, 10) }, new[] { (0, 1, new[] { new Vector2(-50, 10), new Vector2(50, 10) }) });
        var far = RoadGraph.Make(new[] { new Vector2(-50, 200), new Vector2(50, 200) }, new[] { (0, 1, new[] { new Vector2(-50, 200), new Vector2(50, 200) }) });
        var none = new HashSet<string>();
        var nobody = Array.Empty<Vector3>();
        var c = TrapHouseChoice.Candidates(houses, near, Data.Choice.RoadMaxM, none, nobody).Single();
        Assert.Equal(10f, c.RoadDistanceM, 2);
        Assert.True(Vector2.Distance(c.RoadGround, new Vector2(6, 10)) < 1e-3f);
        Assert.Empty(TrapHouseChoice.Candidates(houses, far, Data.Choice.RoadMaxM, none, nobody));
        Assert.Empty(TrapHouseChoice.Candidates(houses, near, Data.Choice.RoadMaxM, new HashSet<string> { "w1" }, nobody));
        Assert.Empty(TrapHouseChoice.Candidates(houses, near, Data.Choice.RoadMaxM, none, new[] { new Vector3(3, 100, -3) }));
        Assert.Single(TrapHouseChoice.Candidates(houses, near, Data.Choice.RoadMaxM, none, new[] { new Vector3(3, 100, 30) })); // someone outside: still free
    }

    /// <summary>Every hollow house of a real map (TRAP_HOUSE_MAP=out/village/map, the full village) keeps the rules; without the variable nothing is checked.</summary>
    [Fact]
    public void Every_house_of_the_map_keeps_the_rules()
    {
        var dir = Environment.GetEnvironmentVariable("TRAP_HOUSE_MAP");
        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "interiors.json"))) return;
        var houses = HouseFloorPlan.Parse(File.ReadAllText(Path.Combine(dir, "interiors.json"))).Where(h => h.Rooms.Length > 0).ToList();
        Assert.NotEmpty(houses);
        foreach (var h in houses)
        {
            var d = TrapHousePlanner.Plan(h, Data, 11);
            Keeps(h, d);
        }
    }
}

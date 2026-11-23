using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// Seat cover and pane kind (ADR 0035, issues 301 and 325): every cover (armoured, glass, exposed) against every pane kind (normal, reinforced; intact,
/// shattered) and the shot's way (through a pane, past one, out by one). The server's own arithmetic: <see cref="PaneRules.Pass"/>,
/// <see cref="RiderRules.BulletsReach"/>, <see cref="GunnerCover"/>. A box car stands at the origin facing -Z, 2 m wide, 4 m long, 2 m high; the rider
/// sits at its middle and a shooter fires from the right (+X) at chest height.
/// </summary>
public class SeatCoverTests
{
    const ushort Rider = 7, Shooter = 1;
    static readonly Vector3 Eye = new(20, 1.2f, 0);
    static readonly Vector3 Left = new(-1, 0, 0); // fired at the car's right side

    static VehicleDef Car(SeatCover cover, GlassKind rightPane, GlassKind leftPane = GlassKind.Normal, bool withPanes = true)
    {
        var v = new VehicleDef
        {
            Id = "t", HalfWidth = 1, HalfLength = 2, Height = 2,
            Seats = new() { new VehicleSeatDef { Role = "driver", Offset = new float[] { 0, 0.3f, 0 }, Cover = cover } },
        };
        if (withPanes)
        {
            v.Glass.Add(Pane("right", 1f, new float[] { 1, 0, 0 }, rightPane));
            v.Glass.Add(Pane("left", -1f, new float[] { -1, 0, 0 }, leftPane));
        }
        return v;
    }

    static VehicleGlassDef Pane(string id, float x, float[] normal, GlassKind kind) =>
        new() { Id = id, Centre = new float[] { x, 1.2f, 0 }, Normal = normal, Size = new float[] { 1.2f, 0.8f }, Kind = kind };

    /// <summary>Whom a bullet fired from <paramref name="eye"/> along <paramref name="dir"/> hits, the pass it had, and the world distance it stopped at.</summary>
    static (RayHit? Hit, HullPass Pass) Fire(VehicleDef v, Func<int, bool>? shattered = null, Vector3? eye = null, Vector3? dir = null)
    {
        var o = eye ?? Eye; var d = dir ?? Left;
        var wall = VehicleBox.Ray(o, d, 100, Vector3.Zero, 0, v.HalfWidth, v.HalfLength, v.Height)!.Value.Distance;
        var pass = PaneRules.Pass(v, shattered ?? (_ => false), o, d, wall, new GlassConfig().VehicleReach, 100);
        var stop = pass.Kind == HullPassKind.Through ? float.PositiveInfinity : pass.T; // nothing else stands beyond
        var rider = new HitTarget(Rider, new Vector3(0, 0, 0) + new Vector3(v.Seats[0].Offset[0], v.Seats[0].Offset[1] - 0.3f, v.Seats[0].Offset[2]), Stance.Stand, 0);
        var targets = RiderRules.BulletsReach(v, 0) ? new List<HitTarget> { rider } : new List<HitTarget>(); // an armoured rider is no target of a bullet
        ExposedRiders? exposed = v.CoverOf(0) == SeatCover.Exposed ? new ExposedRiders(new[] { rider }) : null;
        return (GunnerCover.Raycast(o, d, 100, stop, targets, Shooter, exposed, float.PositiveInfinity), pass);
    }

    // ---- armoured: only explosive damage ----

    [Theory]
    [InlineData(GlassKind.Normal, false)]
    [InlineData(GlassKind.Normal, true)]
    [InlineData(GlassKind.Reinforced, false)]
    [InlineData(GlassKind.Reinforced, true)]
    public void An_armoured_rider_is_never_hit_by_a_bullet_whatever_the_pane(GlassKind kind, bool shattered)
    {
        var (hit, _) = Fire(Car(SeatCover.Armoured, kind, kind), _ => shattered);
        Assert.Null(hit);
    }

    [Fact]
    public void Armoured_is_the_only_cover_no_bullet_reaches_and_a_seat_that_says_nothing_is_glass()
    {
        var v = Car(SeatCover.Armoured, GlassKind.Normal);
        Assert.False(RiderRules.BulletsReach(v, 0));
        Assert.True(RiderRules.BulletsReach(Car(SeatCover.Glass, GlassKind.Normal), 0));
        Assert.True(RiderRules.BulletsReach(Car(SeatCover.Exposed, GlassKind.Normal), 0));
        Assert.True(RiderRules.BulletsReach(new VehicleDef(), 0)); // a seat that is not a seat of this vehicle: a body on foot
        Assert.Equal(SeatCover.Glass, new VehicleSeatDef().Cover ?? SeatCover.Glass);
        Assert.Null(new VehicleSeatDef().Cover); // not declared: the contract test wants it written
    }

    // ---- glass: through a pane, as its kind says ----

    [Fact]
    public void A_glass_rider_behind_a_normal_pane_is_hit_at_once_and_the_pane_is_crossed()
    {
        var (hit, pass) = Fire(Car(SeatCover.Glass, GlassKind.Normal));
        Assert.Equal(Rider, hit!.Value.Id);
        Assert.Equal(HullPassKind.Through, pass.Kind);
        Assert.Equal(0, pass.EntryPane); // the right pane takes the bullet and it goes on
    }

    [Fact]
    public void A_glass_rider_behind_an_intact_reinforced_pane_is_not_hit_and_the_pane_stops_the_bullet()
    {
        var (hit, pass) = Fire(Car(SeatCover.Glass, GlassKind.Reinforced));
        Assert.Null(hit);
        Assert.Equal(HullPassKind.Pane, pass.Kind);
        Assert.InRange(pass.T, 19f - 0.01f, 19f + 0.01f); // the pane at x = 1, 19 m from the muzzle at x = 20
    }

    [Fact]
    public void A_glass_rider_behind_a_shattered_reinforced_pane_is_hit_with_full_damage()
    {
        var (hit, pass) = Fire(Car(SeatCover.Glass, GlassKind.Reinforced, GlassKind.Reinforced), shattered: _ => true);
        Assert.Equal(Rider, hit!.Value.Id);
        Assert.Equal(HullPassKind.Through, pass.Kind);
    }

    [Fact]
    public void Only_the_pane_the_ray_crosses_counts_a_shattered_one_beside_it_does_not_open_the_way()
    {
        // right pane reinforced and whole, the left one shattered: the way in is the right pane
        var (hit, pass) = Fire(Car(SeatCover.Glass, GlassKind.Reinforced, GlassKind.Reinforced), shattered: i => i == 1);
        Assert.Null(hit);
        Assert.Equal(HullPassKind.Pane, pass.Kind);
    }

    [Fact]
    public void A_ray_that_meets_the_hull_where_there_is_no_pane_hits_no_glass_rider()
    {
        var (hit, pass) = Fire(Car(SeatCover.Glass, GlassKind.Normal, GlassKind.Normal), eye: new Vector3(20, 1.2f, 1.7f)); // the pane is 0.6 m either side of z 0
        Assert.Null(hit);
        Assert.Equal(HullPassKind.Hull, pass.Kind);
        Assert.Equal(-1, pass.EntryPane);
    }

    [Fact]
    public void A_vehicle_with_no_panes_hides_a_glass_seat_as_before()
    {
        var (hit, pass) = Fire(Car(SeatCover.Glass, GlassKind.Normal, withPanes: false));
        Assert.Null(hit);
        Assert.Equal(HullPassKind.Hull, pass.Kind);
    }

    [Fact]
    public void A_bullet_in_by_a_normal_pane_that_would_leave_by_an_intact_reinforced_one_hits_the_rider_and_stops_at_the_exit()
    {
        var (hit, pass) = Fire(Car(SeatCover.Glass, GlassKind.Normal, GlassKind.Reinforced));
        Assert.Equal(Rider, hit!.Value.Id);
        Assert.Equal(HullPassKind.Pane, pass.Kind);
        Assert.Equal(0, pass.EntryPane); // the way in took its damage
        Assert.InRange(pass.T, 21f - 0.01f, 21f + 0.01f); // the exit pane at x = -1
    }

    [Fact]
    public void A_bullet_that_would_leave_by_a_shattered_reinforced_pane_flies_on()
    {
        var (_, pass) = Fire(Car(SeatCover.Glass, GlassKind.Normal, GlassKind.Reinforced), shattered: i => i == 1);
        Assert.Equal(HullPassKind.Through, pass.Kind);
        Assert.Equal(1, pass.ExitPane);
    }

    [Fact]
    public void An_open_side_is_never_stopped_a_reinforced_pane_elsewhere_does_not_matter()
    {
        var v = Car(SeatCover.Glass, GlassKind.Reinforced, GlassKind.Reinforced, withPanes: false);
        v.OpenSides.Add(Pane("open", 1f, new float[] { 1, 0, 0 }, GlassKind.Reinforced)); // an opening has no kind in play
        var (hit, pass) = Fire(v);
        Assert.Equal(Rider, hit!.Value.Id);
        Assert.Equal(-1, pass.EntryPane);
    }

    // ---- exposed: unchanged ----

    [Theory]
    [InlineData(GlassKind.Normal)]
    [InlineData(GlassKind.Reinforced)]
    public void An_exposed_rider_is_hit_whatever_the_pane_kind_as_before(GlassKind kind)
    {
        var (hit, _) = Fire(Car(SeatCover.Exposed, kind, kind)); // straight at the pane: an intact reinforced one stops the hull's ray, not the ray at an exposed body
        Assert.Equal(Rider, hit!.Value.Id);
    }

    // ---- kinds: hit points ----

    [Fact]
    public void A_reinforced_pane_has_more_hit_points_than_a_normal_one_and_its_own_hp_wins()
    {
        var cfg = GlassConfig.Load(Path.Combine(AppContext.BaseDirectory, "data", "glass.json"));
        Assert.Equal(cfg.VehiclePaneHp, cfg.PaneHp(GlassKind.Normal));
        Assert.True(cfg.PaneHp(GlassKind.Reinforced) >= 250 && cfg.PaneHp(GlassKind.Reinforced) <= 400);
        Assert.Equal(60f, new GlassConfig().PaneHp(GlassKind.Normal));
        Assert.True(new GlassConfig().PaneHp(GlassKind.Reinforced) > new GlassConfig().PaneHp(GlassKind.Normal));
        Assert.Equal(500f, GlassConfig.Parse("{ \"version\": 1, \"kinds\": { \"reinforced\": { \"hp\": 500 } } }").PaneHp(GlassKind.Reinforced));
        Assert.Throws<DataFileException>(() => GlassConfig.Parse("{ \"version\": 1, \"kinds\": { \"bulletproof\": { \"hp\": 500 } } }"));
        Assert.Throws<DataFileException>(() => GlassConfig.Parse("{ \"version\": 1, \"kinds\": { \"reinforced\": { \"hp\": 0 } } }"));
    }

    [Fact]
    public void A_pane_that_does_not_say_its_kind_is_normal()
    {
        var f = Parse("\"seats\": [{ \"role\": \"driver\", \"cover\": \"glass\" }], \"glass\": [{ \"id\": \"a\", \"centre\": [0, 1, 0], \"size\": [1, 1] }, { \"id\": \"b\", \"centre\": [0, 1, 0], \"size\": [1, 1], \"kind\": \"reinforced\" }]");
        Assert.Equal(GlassKind.Normal, f.Vehicles[0].Glass[0].Kind);
        Assert.Equal(GlassKind.Reinforced, f.Vehicles[0].Glass[1].Kind);
        Assert.Throws<DataFileException>(() => Parse("\"glass\": [{ \"id\": \"a\", \"centre\": [0, 1, 0], \"size\": [1, 1], \"kind\": \"bulletproof\" }]"));
    }

    // ---- data: the three spellings of cover ----

    static VehiclesFile Parse(string extra)
    {
        var json = "{ \"version\": 1, \"vehicles\": [ { \"id\": \"t\", \"top_speed\": 20, \"acceleration\": 3, \"brake\": 8, \"turn_radius\": 4, \"max_lateral_accel\": 8, \"hp\": 100, \"spawn_count\": 1, " + extra + " } ] }";
        return VehiclesFile.Validate(DataFile.Parse<VehiclesFile>(json, "test"));
    }

    [Fact]
    public void A_seat_declares_its_cover_and_the_old_exposed_flag_reads_as_the_exposed_cover()
    {
        var v = Parse("\"seats\": [{ \"role\": \"driver\", \"cover\": \"armoured\" }, { \"role\": \"passenger\", \"exposed\": true }, { \"role\": \"passenger\" }]").Vehicles[0];
        Assert.Equal(SeatCover.Armoured, v.DeclaredCover(0));
        Assert.Equal(SeatCover.Exposed, v.DeclaredCover(1));
        Assert.True(v.Seats[1].Exposed);
        Assert.Null(v.DeclaredCover(2));
        Assert.Equal(SeatCover.Glass, v.CoverOf(2));
        Assert.False(v.SeatExposed(0));
        Assert.True(v.SeatExposed(1));
    }

    [Fact]
    public void Exposed_gunner_is_the_shorthand_for_the_gunner_seats_cover_and_cannot_contradict_it()
    {
        var v = Parse("\"exposed_gunner\": true, \"gun\": { \"damage\": 5, \"fire_interval\": 0.1, \"range\": 100, \"magazine\": 10 }, \"seats\": [{ \"role\": \"driver\", \"cover\": \"glass\" }, { \"role\": \"gunner\" }]").Vehicles[0];
        Assert.Equal(SeatCover.Exposed, v.DeclaredCover(1));
        Assert.Equal(SeatCover.Glass, v.DeclaredCover(0));
        Assert.Throws<DataFileException>(() => Parse("\"exposed_gunner\": true, \"gun\": { \"damage\": 5, \"fire_interval\": 0.1, \"range\": 100, \"magazine\": 10 }, \"seats\": [{ \"role\": \"driver\" }, { \"role\": \"gunner\", \"cover\": \"armoured\" }]"));
    }

    [Fact]
    public void The_shipped_covers_are_the_ones_decided_for_the_vehicles()
    {
        var all = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json")).Vehicles.ToDictionary(v => v.Id);
        static SeatCover?[] Covers(VehicleDef v) => Enumerable.Range(0, v.Seats.Count).Select(i => v.DeclaredCover(i)).ToArray();
        Assert.Equal(new SeatCover?[] { SeatCover.Armoured, SeatCover.Exposed }, Covers(all["flak"]));
        Assert.Equal(new SeatCover?[] { SeatCover.Glass, SeatCover.Exposed, SeatCover.Exposed }, Covers(all["utility_helicopter"]));
        Assert.Equal(new SeatCover?[] { SeatCover.Glass, SeatCover.Glass, SeatCover.Glass, SeatCover.Glass, SeatCover.Exposed }, Covers(all["humvee"]));
        Assert.All(Covers(all["amphibious"]), c => Assert.Equal(SeatCover.Exposed, c));
        foreach (var id in new[] { "quad", "motorcycle", "harvester" }) Assert.All(Covers(all[id]), c => Assert.Equal(SeatCover.Exposed, c));
        foreach (var id in new[] { "car", "golf_mk2", "bus", "scud" }) Assert.All(Covers(all[id]), c => Assert.Equal(SeatCover.Glass, c));
        Assert.All(all.Values.SelectMany(v => v.Glass), g => Assert.Equal(GlassKind.Normal, g.Kind)); // no reinforced pane yet: the data keeps today's glass
    }
}

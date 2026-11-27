using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Jobs;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scenery;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>The simple Jobs (docs/cartel.md "Simple jobs"): the spot claims, the Delivery (#494) and the Hit (#497).</summary>
public class SimpleJobsTests
{
    public SimpleJobsTests() => StateMachineDiagnostics.Strict = true;

    const float Dt = 1f / 60f;

    // the Contact at the origin; drops at 20 m (too near), 200 m and 210 m east (close together), 400 m north; hits at 300 m west and 320 m west
    const string Points = """
    {"version": 2, "zones": [
      {"kind": "cartel_drop", "areas": ["Z-1"], "area_m2": 600, "points": [[20, 0, 0], [200, 0, 0], [210, 0, 0], [0, 400, 0]]},
      {"kind": "cartel_hit", "areas": ["Z-2"], "area_m2": 600, "points": [[-300, 0, 0], [-320, 0, 0]]}
    ]}
    """;

    static readonly CartelMap Map = CartelMap.From(ZonePoints.Parse(Points), new ContactPose(Vector3.Zero, new Vector2(0, 1)));

    sealed class Parties : IJobParties
    {
        public readonly Dictionary<byte, List<ushort>> Squads = new();
        public readonly HashSet<ushort> Present = new() { 1, 2, 3, 4, 5 };
        public JobOwner OwnerOf(ushort p) => Squads.FirstOrDefault(kv => kv.Value.Contains(p)) is { Value: not null } kv ? JobOwner.Squad(kv.Key) : JobOwner.Solo(p);
        public ushort LeaderOf(JobOwner o) => MembersOf(o) is { Count: > 0 } m ? m[0] : (ushort)0;
        public IReadOnlyList<ushort> MembersOf(JobOwner o) => o.Kind == JobOwnerKind.Solo
            ? (Present.Contains(o.Id) ? new[] { o.Id } : Array.Empty<ushort>())
            : Squads.TryGetValue((byte)o.Id, out var m) ? m.ToList() : new List<ushort>();
    }

    sealed class Flat : IDuffelWorld
    {
        public bool Blocked(Vector3 a, Vector3 b, out Vector3 at, out Vector3 normal) { at = default; normal = default; return false; }
        public float? GroundY(Vector3 at, float up, float down) => 0;
    }

    /// <summary>Players as feet and a held key; Duffels in a real book.</summary>
    sealed class World : IDeliveryWorld, IHitWorld
    {
        public readonly WorldEntityManager Entities = new();
        public readonly Dictionary<ushort, Vector3> At = new();
        public readonly HashSet<ushort> Holding = new();
        public FakeCrew? Crew;
        public HitCrewOrder? Order;
        public World() => Duffels = new DuffelBook(DuffelsFile.Default, Entities, new Flat());
        public DuffelBook Duffels { get; }
        public Duffel? Give(DuffelKind kind, IReadOnlySet<ushort> party, ushort carrier) => Duffels.Give(kind, party, carrier, At.GetValueOrDefault(carrier));
        public void RemoveParty(IReadOnlySet<ushort> party) => Duffels.RemoveParty(party);
        public Vector3? Feet(ushort p) => At.TryGetValue(p, out var f) ? f : null;
        public bool UseHeld(ushort p) => Holding.Contains(p);
        public Func<ushort, bool>? Hostile;
        public IHitCrew? Spawn(HitCrewOrder order, Func<ushort, bool> hostileTo) { Order = order; Hostile = hostileTo; return Crew = new FakeCrew { DealerAt = order.Spot }; }
    }

    sealed class FakeCrew : IHitCrew
    {
        public bool DealerPresent { get; set; } = true;
        public bool DealerAlive { get; set; } = true;
        public Vector3 DealerAt { get; set; }
        public int Removed;
        public void Remove() => Removed++;
    }

    static (JobBook Book, Parties Parties, World World, DeliveryJobKind Delivery, HitJobKind Hit) Setup(int seed = 1)
    {
        var parties = new Parties();
        var world = new World();
        var data = CartelFile.Default;
        var delivery = new DeliveryJobKind(data, parties, () => Map, new Random(seed), world);
        var hit = new HitJobKind(data, parties, () => Map, new Random(seed), world);
        var book = new JobBook();
        book.Register(delivery); book.Register(hit);
        return (book, parties, world, delivery, hit);
    }

    static Job Start(JobBook book, IJobParties parties, IJobKind kind, ushort leader, int tier = 1) =>
        book.Start(CartelRules.Giver, kind, tier, parties.OwnerOf(leader), leader, out var why) ?? throw new Xunit.Sdk.XunitException(why);

    static void Ticks(JobBook book, IJobParties parties, List<JobEnded> ended, int n)
    {
        for (var i = 0; i < n && book.Running.Count > 0; i++) book.Tick(Dt, parties, ended);
    }

    [Fact]
    public void The_shipped_cartel_json_reads_both_kinds()
    {
        var file = CartelFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "cartel.json"));
        var parties = new Parties();
        Assert.Equal(240f, new DeliveryJobKind(file, parties, () => Map, new Random(1), new World()).Seconds(1));
        Assert.Equal(300f, new HitJobKind(file, parties, () => Map, new Random(1), new World()).Seconds(1));
    }

    [Fact]
    public void A_claim_keeps_from_the_Contact_and_apart_from_other_jobs_and_runs_out()
    {
        for (var seed = 0; seed < 10; seed++)
        {
            var (book, parties, world, delivery, _) = Setup(seed);
            var a = Start(book, parties, delivery, 1);
            var b = Start(book, parties, delivery, 2);
            var drops = new[] { a, b }.Select(j => ((DeliveryJobKind.Run)j.Objective).Drop).ToList();
            Assert.DoesNotContain(new Vector3(20, 0, 0), drops); // too near the Contact
            Assert.True(Vector2.Distance(new(drops[0].X, drops[0].Z), new(drops[1].X, drops[1].Z)) >= delivery.Numbers.ApartM);
            Assert.Contains(new Vector3(0, 0, -400), drops); // 200 and 210 east are within apart_m of one another: one of them, and the north one
            Assert.Null(book.Start(CartelRules.Giver, delivery, 1, JobOwner.Solo(3), 3, out var why));
            Assert.Contains("free", why);
            Assert.Null(world.Duffels.CarriedBy(3)); // a refused start gives nothing
            Assert.Equal(2, CartelSpots.Held(book, Map).Count);
        }
    }

    [Fact]
    public void A_delivery_gives_the_leader_its_duffel_and_a_hold_at_the_drop_hands_it_over()
    {
        var (book, parties, world, delivery, _) = Setup();
        world.At[1] = Vector3.Zero;
        var job = Start(book, parties, delivery, 1);
        var o = (DeliveryJobKind.Run)job.Objective;
        Assert.Same(o.Duffel, world.Duffels.CarriedBy(1));
        Assert.Equal(DuffelKind.Product, o.Duffel.Kind);
        Assert.Equal("Take the duffel to the drop point", o.Objective(job));
        Assert.Equal(o.Drop, o.Marker(job));
        var ended = new List<JobEnded>();

        world.Holding.Add(1); // the key held away from the drop does nothing
        Ticks(book, parties, ended, 600);
        Assert.Empty(ended);

        world.At[1] = o.Drop + new Vector3(3, 0, 0); // within radius_m
        world.Duffels.Follow(1, world.At[1]);
        Assert.Equal("Hold {interact} to hand the duffel over", o.Objective(job)); // the client puts the bound key in
        Assert.Equal("Hold F to hand the duffel over", JobText.Fill(o.Objective(job), "F"));
        var need = (int)MathF.Ceiling(delivery.Numbers.HoldSeconds / Dt);
        Ticks(book, parties, ended, need - 5);
        Assert.Empty(ended);
        Ticks(book, parties, ended, 10);
        Assert.True(Assert.Single(ended).Succeeded);
        Assert.Equal(0, world.Duffels.Count); // the duffel goes with the job
    }

    [Fact]
    public void Letting_go_of_the_key_starts_the_hold_over()
    {
        var (book, parties, world, delivery, _) = Setup();
        world.At[1] = Vector3.Zero;
        var job = Start(book, parties, delivery, 1);
        var o = (DeliveryJobKind.Run)job.Objective;
        world.At[1] = o.Drop;
        var ended = new List<JobEnded>();
        var need = (int)MathF.Ceiling(delivery.Numbers.HoldSeconds / Dt);
        world.Holding.Add(1);
        Ticks(book, parties, ended, need - 5);
        world.Holding.Clear();
        Ticks(book, parties, ended, 1);
        world.Holding.Add(1);
        Ticks(book, parties, ended, need - 5);
        Assert.Empty(ended);
        Ticks(book, parties, ended, 10);
        Assert.Single(ended);
    }

    [Fact]
    public void A_dropped_duffel_is_marked_and_a_squad_mate_can_carry_it_home()
    {
        var (book, parties, world, delivery, _) = Setup();
        parties.Squads[1] = new() { 1, 2 };
        world.At[1] = Vector3.Zero; world.At[2] = Vector3.Zero;
        var job = Start(book, parties, delivery, 1);
        var o = (DeliveryJobKind.Run)job.Objective;
        Assert.True(world.Duffels.Drop(1, new Vector3(5, 0, 5)));
        Assert.Equal("Pick the duffel up", o.Objective(job));
        Assert.Equal(new Vector3(5, 0, 5), o.Marker(job));
        Assert.True(world.Duffels.PickUp(2, new Vector3(5, 0, 5))); // the mate is of the party (read live)
        world.At[2] = o.Drop; world.Duffels.Follow(2, o.Drop);
        world.Holding.Add(2);
        var ended = new List<JobEnded>();
        Ticks(book, parties, ended, 200);
        Assert.True(Assert.Single(ended).Succeeded);
    }

    [Fact]
    public void A_mate_who_joins_midway_may_touch_the_duffel_and_a_failed_delivery_takes_it_away()
    {
        var (book, parties, world, delivery, _) = Setup();
        parties.Squads[1] = new() { 1 };
        world.At[1] = Vector3.Zero;
        var job = Start(book, parties, delivery, 1);
        var o = (DeliveryJobKind.Run)job.Objective;
        Assert.False(o.Duffel.MayTouch(3));
        parties.Squads[1].Add(3);
        Assert.True(o.Duffel.MayTouch(3));
        var ended = new List<JobEnded>();
        book.PlayerDied(1, parties, ended);
        Assert.False(Assert.Single(ended).Succeeded);
        Assert.Equal(0, world.Duffels.Count);
    }

    [Fact]
    public void Guards_grow_with_the_tier_and_the_party_up_to_the_cap()
    {
        var (_, _, _, _, hit) = Setup();
        Assert.Equal((1, 2), hit.Guards(1, 1));
        Assert.Equal((4, 4), hit.Guards(5, 1));
        Assert.Equal((4, 2), hit.Guards(1, 4)); // three mates add three Gunmen
        Assert.Equal((7, 3), hit.Guards(5, 4)); // capped at max_guards, Gunmen first
    }

    [Fact]
    public void A_hit_puts_a_crew_at_the_spot_hostile_to_the_party_and_succeeds_when_the_dealer_dies()
    {
        var (book, parties, world, _, hit) = Setup();
        parties.Squads[1] = new() { 1, 2 };
        var job = Start(book, parties, hit, 1, tier: 2);
        var o = (HitJobKind.Run)job.Objective;
        Assert.Equal((3, 2), (world.Order!.Value.Gunmen, world.Order.Value.Enforcers));
        Assert.True(o.Spot.X < -200);
        Assert.True(world.Hostile!(2));
        Assert.False(world.Hostile(4));
        Assert.Equal("Find the rival dealer", o.Objective(job));
        Assert.Equal(o.Spot, o.Marker(job));

        var ended = new List<JobEnded>();
        world.At[2] = o.Spot + new Vector3(10, 0, 0); // a mate comes near: the marker moves onto the dealer
        Ticks(book, parties, ended, 1);
        Assert.Equal(HitPhase.Strike, o.Phase);
        world.Crew!.DealerAt = o.Spot + new Vector3(0, 0, 8);
        Assert.Equal(world.Crew.DealerAt, o.Marker(job));
        Assert.Equal("Kill the rival dealer", o.Objective(job));

        world.Crew.DealerAlive = false; // by anyone's hand
        Ticks(book, parties, ended, 1);
        Assert.True(Assert.Single(ended).Succeeded);
        Assert.Equal(1, world.Crew.Removed);
    }

    [Fact]
    public void A_hit_fails_on_its_timer_and_takes_the_crew_away()
    {
        var (book, parties, world, _, hit) = Setup();
        var job = Start(book, parties, hit, 1);
        var ended = new List<JobEnded>();
        Ticks(book, parties, ended, (int)(hit.Seconds(1) / Dt) + 10);
        Assert.Equal(JobFailReason.Timer, Assert.Single(ended).Job.Reason);
        Assert.Equal(1, world.Crew!.Removed);
    }

    [Fact]
    public void Per_tier_numbers_are_checked()
    {
        var bad = CartelFile.Parse("""{"version":1,"jobs":{"hit":{"gunmen":[1,2]}}}""");
        Assert.Throws<ShooterCore.Data.DataFileException>(() => new HitJobKind(bad, new Parties(), () => Map, new Random(1), new World()));
        var kind = CartelFile.Parse("""{"version":1,"jobs":{"delivery":{"duffel":"gold"}}}""");
        Assert.Throws<ShooterCore.Data.DataFileException>(() => new DeliveryJobKind(kind, new Parties(), () => Map, new Random(1), new World()));
    }

    [Fact]
    public void A_map_with_no_point_far_enough_from_the_Contact_says_so()
    {
        var book = new JobBook();
        var test = new TestJobKind(CartelFile.Default);
        book.Register(test);
        var job = book.Start(CartelRules.Giver, test, 1, JobOwner.Solo(1), 1, out _)!;
        var contact = Map.Contact!.Value.Position;
        Assert.Null(CartelSpots.Claim(job, book, Map, CartelZones.DropKind, new Random(1), contact, "the Contact", 10_000, 0, out var why));
        Assert.Equal("No drop point is far enough from the Contact on this map.", why);
        Assert.NotNull(CartelSpots.Claim(job, book, Map, CartelZones.HitKind, new Random(1), contact, "the Contact", 100, 0, out _));
        Assert.Null(CartelSpots.Claim(job, book, Map, CartelZones.HitKind, new Random(1), contact, "the Contact", 100, 1_000, out why)); // the other is within apart_m of the one held
        Assert.Equal("No hit spot 100 m or more from the Contact is free now: another job holds every one.", why);
        // a Heist's drop keeps apart from the held hit spot too: every drop point lies within 1 000 m of it
        Assert.Null(CartelSpots.Claim(job, book, Map, CartelZones.DropKind, new Random(1), contact, "the plantation", 100, 1_000, out why));
        Assert.Equal("No drop point 100 m or more from the plantation is free now: another job holds every one.", why);
    }
}

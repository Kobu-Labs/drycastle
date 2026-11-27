using System.Numerics;
using Xunit;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>The Duffel (#494): the data, the carrier's burden, the book (one per player, the party, drop, the one-press throw, the world, cargo volumes) and the key's priority.</summary>
public class DuffelTests
{
    static readonly DuffelsFile Data = DuffelsFile.Default;
    const float Dt = 1f / 30f;
    static readonly Vector3 Eye = new(0, 1.65f, 0);

    /// <summary>Flat ground at y 0, and a wall facing -X at <see cref="WallX"/> when set.</summary>
    sealed class FlatWorld : IDuffelWorld
    {
        public float? WallX;

        public bool Blocked(Vector3 a, Vector3 b, out Vector3 at, out Vector3 normal)
        {
            if (WallX is { } w && a.X < w && b.X >= w)
            {
                var t = (w - a.X) / (b.X - a.X);
                at = Vector3.Lerp(a, b, t); normal = new Vector3(-1, 0, 0);
                return true;
            }
            if (a.Y >= 0 && b.Y < 0)
            {
                var t = a.Y / (a.Y - b.Y);
                at = Vector3.Lerp(a, b, t); normal = Vector3.UnitY;
                return true;
            }
            at = default; normal = default;
            return false;
        }

        public float? GroundY(Vector3 at, float up, float down) => at.Y - down <= 0 && 0 <= at.Y + up ? 0 : null;
    }

    /// <summary>An axis-aligned box that takes the Duffels of one party.</summary>
    sealed class FakeVolume : ICargoVolume
    {
        public Vector3 Min, Max;
        public IReadOnlySet<ushort>? Party;
        public readonly List<Duffel> Loaded = new();

        public bool Contains(Vector3 p) => p.X >= Min.X && p.Y >= Min.Y && p.Z >= Min.Z && p.X <= Max.X && p.Y <= Max.Y && p.Z <= Max.Z;
        public bool Accepts(Duffel d) => ReferenceEquals(d.Party, Party);
        public void Load(Duffel d) => Loaded.Add(d);
    }

    static (DuffelBook Book, WorldEntityManager Entities, FlatWorld World) NewBook()
    {
        var entities = new WorldEntityManager();
        var world = new FlatWorld();
        return (new DuffelBook(Data, entities, world), entities, world);
    }

    static void Fly(WorldEntityManager e, int ticks = 300) { for (var i = 0; i < ticks; i++) e.Tick(Dt); }

    static float Flat(Vector3 p) => new Vector2(p.X, p.Z).Length();

    // ---- the data ----

    [Fact]
    public void The_shipped_file_parses_and_heavier_kinds_are_slower_and_one_range_serves_all()
    {
        var shipped = DuffelsFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "duffels.json"));
        var weed = shipped.Of(DuffelKind.Weed); var cash = shipped.Of(DuffelKind.Cash); var product = shipped.Of(DuffelKind.Product);
        Assert.True(weed.WeightKg < cash.WeightKg && cash.WeightKg < product.WeightKg);
        Assert.True(weed.SpeedScale > cash.SpeedScale && cash.SpeedScale > product.SpeedScale);
        Assert.Equal(8, shipped.ThrowM); // one range for every kind: the weight slows the carrier only
        Assert.Equal(8, DuffelThrow.RangeM(shipped));
    }

    [Fact]
    public void A_file_without_a_kind_is_refused_with_the_field_named()
    {
        var e = Assert.Throws<Drycastle.ShooterCore.Data.DataFileException>(() => DuffelsFile.Parse("""{ "version": 1, "kinds": { "weed": { "weight_kg": 8, "speed_scale": 0.9 } } }"""));
        Assert.Contains("kinds.cash", e.Message);
    }

    // ---- the burden ----

    [Fact]
    public void A_carrier_cannot_sprint_and_walks_at_the_kinds_speed()
    {
        static float Speed(MoveInput i)
        {
            var s = new MoveState { OnFloor = true };
            for (var k = 0; k < 60; k++) { MovementStep.Step(ref s, i, Dt); MovementStep.Integrate(ref s, Dt); }
            return new Vector2(s.Velocity.X, s.Velocity.Z).Length();
        }
        var sprint = new MoveInput { MoveY = 1, Buttons = MoveButtons.Sprint, SpeedScale = 1 };
        var burden = Data.BurdenOf(DuffelKind.Cash);
        Assert.Equal(MoveConsts.SprintSpeed, Speed(sprint), 2);
        Assert.Equal(MoveConsts.WalkSpeed * (float)Data.Of(DuffelKind.Cash).SpeedScale, Speed(burden.Apply(sprint)), 2);
        Assert.Equal(0.85f * (float)Data.Of(DuffelKind.Cash).SpeedScale, burden.Apply(new MoveInput { SpeedScale = 0.85f }).SpeedScale, 4); // on top of the weapon's
        Assert.Equal(sprint, MoveBurden.None.Apply(sprint));
        Assert.Equal(sprint, default(MoveBurden).Apply(sprint));
    }

    // ---- the book ----

    [Fact]
    public void One_per_player_and_only_the_party_picks_one_up()
    {
        var (book, _, _) = NewBook();
        var party = new HashSet<ushort> { 1, 2 };
        var d = book.Give(DuffelKind.Weed, party, 1, Vector3.Zero)!;
        Assert.Equal(DuffelState.Carried, d.State);
        Assert.Null(book.Give(DuffelKind.Cash, party, 1, Vector3.Zero)); // one each
        var lying = book.Place(DuffelKind.Cash, party, new Vector3(1, 0, 0))!;
        Assert.Null(book.Pickable(1, Vector3.Zero)); // carries one
        Assert.Null(book.Pickable(3, Vector3.Zero)); // not of the party
        Assert.Null(book.Pickable(2, new Vector3(10, 0, 0))); // out of reach
        Assert.Same(lying, book.Pickable(2, Vector3.Zero));
        Assert.True(book.PickUp(2, Vector3.Zero));
        Assert.Same(lying, book.CarriedBy(2));
        Assert.Equal((ushort)2, lying.Owner);
    }

    [Fact]
    public void A_drop_lies_on_the_ground_at_the_feet()
    {
        var (book, _, _) = NewBook();
        var d = book.Give(DuffelKind.Product, new HashSet<ushort> { 1 }, 1, new Vector3(3, 0.4f, 4))!;
        Assert.True(book.Drop(1, new Vector3(3, 0.4f, 4)));
        Assert.Equal(DuffelState.Lying, d.State);
        Assert.Equal(new Vector3(3, 0, 4), d.Position);
        Assert.Null(book.CarriedBy(1));
        Assert.False(book.Drop(1, Vector3.Zero));
    }

    [Fact]
    public void A_drop_from_high_up_falls_and_lies_on_the_ground()
    {
        var (book, entities, _) = NewBook();
        var d = book.Give(DuffelKind.Cash, new HashSet<ushort> { 1 }, 1, new Vector3(2, 6, 0))!;
        Assert.True(book.Drop(1, new Vector3(2, 6, 0))); // no ground within the probe: it falls
        Assert.Equal(DuffelState.Flying, d.State);
        Fly(entities);
        Assert.Equal(DuffelState.Lying, d.State);
        Assert.Equal(0, d.Position.Y, 3);
        Assert.Equal(2, d.Position.X, 3);
    }

    [Fact]
    public void Every_change_of_carrier_is_told_whoever_made_it()
    {
        var (book, entities, _) = NewBook();
        var told = new List<(ushort Player, Duffel? Duffel)>();
        book.CarrierChanged += (p, d) => told.Add((p, d));
        var party = new HashSet<ushort> { 1, 2 };
        var given = book.Give(DuffelKind.Weed, party, 1, Vector3.Zero)!;
        var lying = book.Place(DuffelKind.Cash, party, Vector3.Zero)!;
        book.PickUp(2, Vector3.Zero);
        book.Drop(1, Vector3.Zero);
        book.PickUp(1, Vector3.Zero); // the weed it just put down
        book.Throw(1, Eye, 0, 0);
        Assert.Equal(new (ushort, Duffel?)[] { (1, given), (2, lying), (1, null), (1, given), (1, null) }, told);
        told.Clear();
        book.RemoveParty(party); // a Job's end frees its carriers too
        Assert.Equal(new (ushort, Duffel?)[] { (2, null) }, told);
        told.Clear();
        book.Give(DuffelKind.Product, new HashSet<ushort> { 3 }, 3, Vector3.Zero);
        entities.Clear(); // the round's reset
        Assert.Equal(new (ushort, Duffel?)[] { (3, null) }, told.Skip(1));
        Assert.Equal(0, book.CarriedCount);
    }

    [Theory]
    [InlineData(DuffelKind.Weed)]
    [InlineData(DuffelKind.Cash)]
    [InlineData(DuffelKind.Product)]
    public void One_throw_from_a_level_look_lands_the_same_fixed_range_away_for_every_kind(DuffelKind kind)
    {
        var (book, entities, _) = NewBook();
        var d = book.Give(kind, new HashSet<ushort> { 1 }, 1, Vector3.Zero)!;
        Assert.True(book.Throw(1, Eye, 0, 0)); // one press: no charge to give
        Assert.Equal(DuffelState.Flying, d.State);
        Fly(entities);
        Assert.Equal(DuffelState.Lying, d.State);
        Assert.Equal(0, d.Position.Y, 3);
        var range = (float)Data.ThrowM;
        Assert.InRange(Flat(d.Position), range, range + 0.8f); // from the hands, a little in front of the eye
        Assert.False(book.Throw(1, Eye, 0, 0)); // nothing left in the hands
        Assert.True(d.Position.Z < 0); // yaw 0 faces -Z
    }

    [Fact]
    public void A_wall_stops_the_throw_and_it_falls_at_its_foot()
    {
        var (book, entities, world) = NewBook();
        world.WallX = 3;
        var d = book.Give(DuffelKind.Weed, new HashSet<ushort> { 1 }, 1, Vector3.Zero)!;
        book.Throw(1, Eye, -MathF.PI / 2, 0); // yaw -90 degrees faces +X
        Fly(entities);
        Assert.Equal(DuffelState.Lying, d.State);
        Assert.InRange(d.Position.X, 2.5f, 3f);
        Assert.Equal(0, d.Position.Y, 3);
    }

    [Fact]
    public void A_throw_into_an_accepting_volume_loads_it_and_its_owner_counts_it()
    {
        var (book, entities, _) = NewBook();
        var party = new HashSet<ushort> { 1 };
        var van = new FakeVolume { Min = new Vector3(-1, 0, -8), Max = new Vector3(1, 1.5f, -4), Party = party };
        book.AddVolume(van);
        var d = book.Give(DuffelKind.Weed, party, 1, Vector3.Zero)!;
        book.Throw(1, Eye, 0, 0); // about 8 m: through the box (the volume is met on the way)
        Fly(entities);
        Assert.Equal(DuffelState.Loaded, d.State);
        Assert.Same(van, d.Volume);
        Assert.Equal(new[] { d }, van.Loaded);
        Assert.Equal(1, book.LoadedIn(van));
        Assert.Null(book.Pickable(1, d.Position)); // a loaded one is not picked up
    }

    [Fact]
    public void A_volume_of_another_party_does_not_take_it()
    {
        var (book, entities, _) = NewBook();
        var van = new FakeVolume { Min = new Vector3(-1, 0, -8), Max = new Vector3(1, 1.5f, -4), Party = new HashSet<ushort> { 9 } };
        book.AddVolume(van);
        var d = book.Give(DuffelKind.Weed, new HashSet<ushort> { 1 }, 1, Vector3.Zero)!;
        book.Throw(1, Eye, 0, 0);
        Fly(entities);
        Assert.Equal(DuffelState.Lying, d.State);
        Assert.Empty(van.Loaded);
    }

    [Fact]
    public void A_drop_inside_a_volume_lies_on_the_ground_only_a_throw_loads()
    {
        var (book, _, _) = NewBook();
        var party = new HashSet<ushort> { 1 };
        var van = new FakeVolume { Min = new Vector3(-1, 0, -1), Max = new Vector3(1, 1.5f, 1), Party = party };
        book.AddVolume(van);
        var d = book.Give(DuffelKind.Cash, party, 1, Vector3.Zero)!;
        book.Drop(1, new Vector3(0, 0.2f, 0));
        Assert.Equal(DuffelState.Lying, d.State);
        Assert.Empty(van.Loaded);
    }

    [Fact]
    public void The_state_byte_carries_the_state_and_the_kind_and_the_owner_field_the_carrier()
    {
        var (book, entities, _) = NewBook();
        var d = book.Give(DuffelKind.Product, new HashSet<ushort> { 7 }, 7, new Vector3(1, 2, 3))!;
        var s = entities.StateOf(d.Id)!.Value;
        Assert.Equal(DuffelState.Carried, DuffelRules.StateOf(s.State));
        Assert.Equal(DuffelKind.Product, DuffelRules.KindOf(s.State));
        Assert.Equal((ushort)7, s.Owner);
        Assert.False(WorldEntityRegistry.Shared.Find(DuffelRules.TypeId)!.HasHp);
        Assert.Equal(DuffelKind.Cash, DuffelRules.Parse(" Cash "));
        Assert.Null(DuffelRules.Parse("gold"));
    }

    [Fact]
    public void A_jobs_duffels_go_with_its_party_and_free_their_carriers()
    {
        var (book, entities, _) = NewBook();
        var party = new HashSet<ushort> { 1, 2 };
        book.Give(DuffelKind.Weed, party, 1, Vector3.Zero);
        book.Place(DuffelKind.Cash, party, Vector3.One);
        var other = book.Place(DuffelKind.Cash, new HashSet<ushort> { 3 }, Vector3.One)!;
        Assert.Equal(2, book.RemoveParty(party));
        Assert.Null(book.CarriedBy(1));
        Assert.Equal(1, book.Count);
        Assert.Equal(1, entities.Count);
        entities.Clear(); // a round reset clears the match's entities
        Assert.Equal(0, book.Count);
        Assert.Null(book.Find(other.Id));
    }

    // ---- the key and the wire ----

    [Fact]
    public void A_duffel_owns_the_interact_key_after_the_crate_and_before_a_ground_item_and_the_corpse()
    {
        Assert.Equal(InteractOwner.Crate, InteractArbiter.Owner(true, false, false, false, false, duffelInReach: true));
        Assert.Equal(InteractOwner.Duffel, InteractArbiter.Owner(false, true, true, true, false, true, duffelInReach: true));
        Assert.False(InteractArbiter.CorpseShowsPrompt(false, false, false, true, false, duffelInReach: true));
        Assert.Equal(InteractOwner.Corpse, InteractArbiter.Owner(false, false, false, true, false));
    }

    [Fact]
    public void The_notice_and_the_reach_round_trip()
    {
        Assert.True(DuffelCodec.TryDecodeNotice(DuffelCodec.EncodeNotice(DuffelNotice.NoSeat), out var n));
        Assert.Equal(DuffelNotice.NoSeat, n);
        Assert.True(DuffelCodec.TryDecodeNotice(DuffelCodec.EncodeNotice(DuffelNotice.VanReserved), out n));
        Assert.Equal(DuffelNotice.VanReserved, n);
        Assert.False(DuffelCodec.TryDecodeNotice(new byte[] { 200 }, out _));
        Assert.True(DuffelCodec.TryDecodeReach(DuffelCodec.EncodeReach(513), out var id));
        Assert.Equal((ushort)513, id);
        Assert.Equal((byte)42, (byte)EventKind.DuffelNotice);
        Assert.Equal((byte)43, (byte)EventKind.DuffelReach);
    }
}

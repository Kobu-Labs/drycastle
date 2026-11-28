using System.Numerics;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Plantation;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Vehicles;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>The Heists' Receivers (docs/cartel.md "Receivers"): the kind choice, what a Receiver takes and when it leaves, the way out, the wire, the stash house's place, the map's zones, and cartel.json receivers.</summary>
public class ReceiverTests
{
    public ReceiverTests() => StateMachineDiagnostics.Strict = true;

    static readonly CartelFile Shipped = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Cartel;
    static readonly DuffelsFile Duffels = DuffelsFile.Default;
    static readonly IReadOnlySet<ushort> Party = new HashSet<ushort> { 1, 2 };
    static readonly IReadOnlySet<ushort> Others = new HashSet<ushort> { 9 };
    const float Dt = 1f / 30f;
    static readonly Vector3 Eye = new(0, 1.65f, 0);

    sealed class Flat : IDuffelWorld
    {
        public bool Blocked(Vector3 a, Vector3 b, out Vector3 at, out Vector3 normal)
        {
            if (a.Y >= 0 && b.Y < 0) { at = Vector3.Lerp(a, b, a.Y / (a.Y - b.Y)); normal = Vector3.UnitY; return true; }
            at = default; normal = default;
            return false;
        }

        public float? GroundY(Vector3 at, float up, float down) => at.Y - down <= 0 && 0 <= at.Y + up ? 0 : null;
    }

    static ReceiversData Data => Shipped.Receivers;

    /// <summary>A Receiver of the kind 7 m in front of the origin (the 8 m throw lands just past it), taking the party's Duffels, the minimum 2.</summary>
    static (Receiver R, DuffelBook Book, WorldEntityManager Entities) Setup(ReceiverKind kind = ReceiverKind.StashHouse, int min = 2, int capacity = 15, ReceiverPath? path = null)
    {
        ReceiverRules.Register(WorldEntityRegistry.Shared);
        var entities = new WorldEntityManager();
        var book = new DuffelBook(Duffels, entities, new Flat());
        var r = new Receiver(kind, Data, new Vector3(0, 0, -7), 0, Party, min, capacity, path, (float)Duffels.CargoMarginM);
        r.Id = entities.Spawn(r)!.Value;
        book.AddVolume(r);
        return (r, book, entities);
    }

    static Duffel Throw(DuffelBook book, WorldEntityManager entities, IReadOnlySet<ushort> party, ushort player = 1)
    {
        var d = book.Give(DuffelKind.Weed, party, player, Vector3.Zero)!;
        book.Throw(player, Eye, 0, 0);
        for (var i = 0; i < 300; i++) entities.Tick(Dt);
        return d;
    }

    // ---- cartel.json receivers ----

    [Fact]
    public void TheShippedReceiversAreTheDefaultsAndCheckOut()
    {
        var d = new ReceiversData();
        var s = Shipped.Receivers;
        Assert.Equal((d.Weights.StashHouse, d.Weights.Helicopter, d.Weights.Boat, d.ReachM, d.ReachUpM), (s.Weights.StashHouse, s.Weights.Helicopter, s.Weights.Boat, s.ReachM, s.ReachUpM));
        Assert.Equal((d.Leave.ClimbM, d.Leave.ClimbMps, d.Leave.FlyMps, d.Leave.ExitMarginM, d.Leave.PitchDeg, d.Leave.SailMps, d.Leave.RampSeconds), (s.Leave.ClimbM, s.Leave.ClimbMps, s.Leave.FlyMps, s.Leave.ExitMarginM, s.Leave.PitchDeg, s.Leave.SailMps, s.Leave.RampSeconds));
        foreach (var kind in ReceiverRules.AllKinds)
        {
            var (a, b) = (d.Of(kind), s.Of(kind));
            Assert.Equal((a.Model, a.Atlas, a.ModelYawDeg, a.AreaM, a.Figure.YawDeg, a.Figure.Look.Idle), (b.Model, b.Atlas, b.ModelYawDeg, b.AreaM, b.Figure.YawDeg, b.Figure.Look.Idle));
            Assert.Equal(a.Figure.Pos, b.Figure.Pos);
            Assert.Equal(a.CargoVolume.Centre, b.CargoVolume.Centre);
            Assert.Equal(a.CargoVolume.Size, b.CargoVolume.Size);
            Assert.Equal(a.CargoVolume.Capacity, b.CargoVolume.Capacity);
            Assert.Equal(a.CargoVolume.Slots, b.CargoVolume.Slots);
        }
    }

    [Theory]
    [InlineData(ReceiverKind.StashHouse)]
    [InlineData(ReceiverKind.Helicopter)]
    [InlineData(ReceiverKind.Boat)]
    public void EveryKindHasFifteenSlotsThatFitInsideItsVolumeAndDoNotOverlapAndEachRestsOnTheFloorOrABag(ReceiverKind kind)
    {
        var v = Data.Of(kind).CargoVolume;
        Assert.Equal(15, v.Slots.Count);
        const float length = 0.56f, depth = 0.28f, height = 0.24f; // a bag, lying along x
        for (var i = 0; i < v.Slots.Count; i++)
        {
            var a = v.Slots[i];
            Assert.InRange(a[0] - length / 2, v.Centre[0] - v.Size[0] / 2 - 0.002f, v.Centre[0] + v.Size[0] / 2);
            Assert.InRange(a[0] + length / 2, v.Centre[0] - v.Size[0] / 2, v.Centre[0] + v.Size[0] / 2 + 0.002f);
            Assert.InRange(a[2] - depth / 2, v.Centre[2] - v.Size[2] / 2 - 0.002f, v.Centre[2] + v.Size[2] / 2);
            Assert.InRange(a[2] + depth / 2, v.Centre[2] - v.Size[2] / 2, v.Centre[2] + v.Size[2] / 2 + 0.002f);
            Assert.InRange(a[1] + height, v.Centre[1] - v.Size[1] / 2, v.Centre[1] + v.Size[1] / 2); // the top of the pile is under the volume's roof
            for (var j = i + 1; j < v.Slots.Count; j++)
            {
                var b = v.Slots[j];
                var apart = MathF.Abs(a[0] - b[0]) >= length - 0.005f || MathF.Abs(a[2] - b[2]) >= depth - 0.005f || MathF.Abs(a[1] - b[1]) >= height - 0.005f;
                Assert.True(apart, $"slots {i} and {j} overlap");
            }
            if (a[1] > v.Slots.Min(s => s[1]) + 0.001f) // a bag above the floor layer lies on one below it
                Assert.Contains(v.Slots, s => MathF.Abs(s[0] - a[0]) < 0.001f && MathF.Abs(s[2] - a[2]) < 0.001f && MathF.Abs(s[1] + height - a[1]) < 0.005f);
        }
    }

    [Fact]
    public void ARunsReceiverTakesAsManyAsTheHeistsMaximumWithAHardCapOfFifteenSlots()
    {
        Assert.Equal(6, Setup(capacity: 6).R.Capacity);
        Assert.Equal(15, Setup(capacity: 40).R.Capacity);
        Assert.Equal(1, Setup(capacity: 0).R.Capacity);
    }

    [Fact]
    public void ABadReceiverSectionIsRefusedWithTheFieldNamed()
    {
        var d = new ReceiversData();
        d.Boat.CargoVolume.Slots.RemoveAt(0);
        Assert.Contains("receivers.boat.cargo_volume", Assert.Throws<DataFileException>(() => d.Validate()).Message);
        d = new ReceiversData();
        d.Helicopter.CargoVolume.Slots[3] = new[] { 9f, 0.7f, 0f, 0f };
        Assert.Contains("slots[3] lies outside", Assert.Throws<DataFileException>(() => d.Validate()).Message);
        d = new ReceiversData { Weights = new ReceiverWeights { StashHouse = 0, Helicopter = 0, Boat = 0 } };
        Assert.Contains("weights", Assert.Throws<DataFileException>(() => d.Validate()).Message);
        d = new ReceiversData();
        d.StashHouse.CargoVolume.Doors.Add(new VehicleCargoDoorDef { Node = "x" });
        Assert.Contains("no doors", Assert.Throws<DataFileException>(() => d.Validate()).Message);
    }

    // ---- the kind choice ----

    [Fact]
    public void TheKindChoiceIsARandomOrderWeightedAmongTheKindsTheMapHasAndNeverAZeroWeight()
    {
        var all = ReceiverRules.AllKinds;
        var order = ReceiverRules.Order(new ReceiverWeights(), all, new Random(1));
        Assert.Equal(3, order.Count);
        Assert.Equal(all.OrderBy(k => k), order.OrderBy(k => k)); // each once

        Assert.Equal(new[] { ReceiverKind.Boat }, ReceiverRules.Order(new ReceiverWeights(), new[] { ReceiverKind.Boat }, new Random(2))); // only what the map has
        var weights = new ReceiverWeights { StashHouse = 0, Helicopter = 1, Boat = 1 };
        for (var seed = 0; seed < 50; seed++) Assert.DoesNotContain(ReceiverKind.StashHouse, ReceiverRules.Order(weights, all, new Random(seed)));
        Assert.Empty(ReceiverRules.Order(new ReceiverWeights { StashHouse = 0, Helicopter = 0, Boat = 0 }, all, new Random(3)));

        var firsts = new Dictionary<ReceiverKind, int>();
        var rng = new Random(7);
        for (var i = 0; i < 3000; i++) { var k = ReceiverRules.Order(new ReceiverWeights { StashHouse = 1, Helicopter = 2, Boat = 1 }, all, rng)[0]; firsts[k] = firsts.GetValueOrDefault(k) + 1; }
        Assert.InRange(firsts[ReceiverKind.Helicopter], 1350, 1650); // half the first picks
        Assert.InRange(firsts[ReceiverKind.StashHouse], 600, 900);
    }

    // ---- loading ----

    [Fact]
    public void AReceiverTakesItsPartysDuffelsThrownIntoItAndNobodyElses()
    {
        var (r, book, entities) = Setup();
        var mine = Throw(book, entities, Party);
        Assert.Equal(DuffelState.Loaded, mine.State);
        Assert.Equal(1, r.Loaded);
        Assert.Same(r, mine.Volume);
        Assert.Equal(ReceiverRules.OwnerOf(r.Id), mine.Owner); // the wire: the Receiver's entity id with bit 15
        var alien = Throw(book, entities, Others, player: 9);
        Assert.Equal(DuffelState.Lying, alien.State); // a stranger's bag stops at it as at a wall
        Assert.Equal(1, r.Loaded);
    }

    [Fact]
    public void AThrowReachesTheReceiversVolumeAnywhereAroundItAndTheSlotsFillInOrder()
    {
        var (r, book, entities) = Setup(ReceiverKind.Helicopter);
        var first = Throw(book, entities, Party);
        var second = Throw(book, entities, Party);
        Assert.Equal((0, 1), (first.Slot, second.Slot));
        Assert.Equal(DuffelState.Loaded, second.State);
        Assert.Equal(first, r.In(0));
        var at = r.SlotAt(0)!.Value; // the slot is where the pose and the data say
        var slot = Data.Helicopter.CargoVolume.Slot(0)!.Value;
        Assert.Equal(r.Position.X + slot.Position.X, at.X, 3);
        Assert.Equal(r.Position.Y + slot.Position.Y, at.Y, 3);
    }

    [Fact]
    public void AFullReceiverRefusesTheNext()
    {
        var (r, book, entities) = Setup(capacity: 2);
        Throw(book, entities, Party); Throw(book, entities, Party);
        var third = Throw(book, entities, Party);
        Assert.Equal(DuffelState.Lying, third.State);
        Assert.Equal(2, r.Loaded);
    }

    [Fact]
    public void NothingIsTakenOutOfAReceiver()
    {
        var (r, book, entities) = Setup();
        Throw(book, entities, Party);
        Assert.Null(r.TakeOutDistance(new Vector3(0, 0, -5)));
        Assert.Null(book.Takeable(1, new Vector3(0, 0, -5))); // not even the party's, standing right at it
        Assert.Null(book.TakeOut(1, new Vector3(0, 0, -5)));
        Assert.Equal(1, r.Loaded);
    }

    // ---- the end rules ----

    [Fact]
    public void TheLeaderMaySendItOffOnlyWithTheMinimumOnBoardAndTheCountIsFrozenThen()
    {
        var (r, book, entities) = Setup(min: 2);
        Assert.False(r.CanSend);
        Throw(book, entities, Party);
        Assert.False(r.Send()); // under the minimum
        Assert.Equal(ReceiverPhase.Receiving, r.Phase);
        Throw(book, entities, Party);
        Assert.True(r.CanSend);
        Assert.True(r.Send());
        Assert.Equal((ReceiverPhase.Leaving, 2, true), (r.Phase, r.SentWith, r.Sent));
        Assert.False(r.Send()); // once
        var late = Throw(book, entities, Party);
        Assert.Equal(DuffelState.Lying, late.State); // it takes no more once it left
        Assert.Equal(2, r.SentWith);
    }

    [Fact]
    public void TheBagsOnALeavingReceiverSurviveTheJobsEndAndAreRemovedWhenItIsGone()
    {
        var (r, book, entities) = Setup(ReceiverKind.StashHouse, min: 2);
        var a = Throw(book, entities, Party);
        var b = Throw(book, entities, Party);
        var lying = book.Place(DuffelKind.Cash, Party, new Vector3(5, 0, 5))!;
        Assert.True(r.Send());
        Assert.Equal(1, book.RemoveParty(Party, keep: r)); // the Job ended: only the lying one went, the count says so
        Assert.Null(book.Find(lying.Id));
        Assert.Same(a, book.Find(a.Id));
        Assert.Same(b, book.Find(b.Id));
        Assert.Equal(DuffelState.Loaded, a.State);
        entities.Tick(Dt); // the buyer takes the bags, the Receiver is Gone
        Assert.Equal(ReceiverPhase.Gone, r.Phase);
        Assert.Equal(2, book.RemoveLoadedIn(r)); // the server removes them once it is gone
        Assert.Null(book.Find(a.Id));
        Assert.Null(book.Find(b.Id));
    }

    [Fact]
    public void TheAsksOfTheKeyAtTheCartelMan()
    {
        Assert.Equal(ReceiverAsk.Leave, ReceiverRules.Ask(true, 3, 3));
        Assert.Equal(ReceiverAsk.NeedMore, ReceiverRules.Ask(true, 2, 3));
        Assert.Equal(ReceiverAsk.NotLeader, ReceiverRules.Ask(false, 9, 3)); // a mate's press never sends it
    }

    [Fact]
    public void TheCartelManIsReachedFromTheGroundNearHimWhileItReceivesAndNotAfter()
    {
        var (r, _, _) = Setup(ReceiverKind.Boat, min: 0);
        var man = r.ManAt;
        Assert.True(r.InReach(man + new Vector3(2, 0, 0)));
        Assert.False(r.InReach(man + new Vector3(Data.ReachM + 0.5f, 0, 0)));
        Assert.False(r.InReach(man + new Vector3(0, Data.ReachUpM + 0.5f, 0))); // not from a roof
        Assert.True(r.Send());
        Assert.False(r.InReach(man)); // he is leaving
    }

    [Fact]
    public void AStashHousesBuyerTakesTheBagsAtOnceAndItIsGoneOnTheNextTick()
    {
        var (r, book, entities) = Setup(ReceiverKind.StashHouse, min: 1);
        var bag = Throw(book, entities, Party);
        var gone = 0;
        r.Gone += _ => gone++;
        Assert.True(r.Send());
        entities.Tick(Dt);
        Assert.Equal((ReceiverPhase.Gone, 1), (r.Phase, gone));
        Assert.Equal(WorldDespawnReason.Removed, r.Ended); // the manager despawned it
        Assert.Null(entities.StateOf(r.Id));
        Assert.Equal(1, r.SentWith);
        Assert.NotNull(bag);
    }

    // ---- the way out ----

    [Fact]
    public void AHelicopterClimbsTurnsFliesOffTheMapAndIsGone()
    {
        var leave = Data.Leave;
        var start = new Vector3(0, 10, -7);
        var path = ReceiverPath.Helicopter(start, new Vector2(2000, -7), leave);
        var (r, book, entities) = Setup(ReceiverKind.Helicopter, min: 1, path: path);
        Throw(book, entities, Party);
        Assert.True(r.Send());
        var peak = 0f; var turned = false; var pitched = false; var gone = 0;
        r.Gone += _ => gone++;
        var seconds = 0f;
        while (r.Phase == ReceiverPhase.Leaving && seconds < 200)
        {
            entities.Tick(Dt); seconds += Dt;
            peak = MathF.Max(peak, r.Position.Y - 0f);
            turned |= MathF.Abs(MathF.IEEERemainder(r.Yaw + MathF.PI / 2, MathF.Tau)) < 0.2f; // facing +X, east: the way it flies
            pitched |= r.Pitch < -0.1f;
        }
        Assert.Equal(ReceiverPhase.Gone, r.Phase);
        Assert.Equal(1, gone);
        Assert.True(peak >= 10 + leave.ClimbM - 0.5f, "it climbed the 60 m first");
        Assert.True(turned && pitched, "it turned to face its way and put its nose down");
        Assert.InRange(seconds, 10, 120);
        Assert.True(r.Position.X > 1990, "and it ended over the exit point");
    }

    [Fact]
    public void ABoatSailsItsRouteAtTheSurfaceAndIsGoneAtItsEnd()
    {
        var route = new[] { new Vector3(0, 185.2f, -7), new Vector3(0, 185.2f, 50), new Vector3(60, 185.2f, 120) };
        var (r, book, entities) = Setup(ReceiverKind.Boat, min: 1, path: ReceiverPath.Boat(route, Data.Leave));
        Throw(book, entities, Party);
        Assert.True(r.Send());
        var maxDeviation = 0f;
        for (var t = 0f; t < 300 && r.Phase == ReceiverPhase.Leaving; t += Dt)
        {
            entities.Tick(Dt);
            maxDeviation = MathF.Max(maxDeviation, MathF.Abs(r.Position.Y - 185.2f));
        }
        Assert.Equal(ReceiverPhase.Gone, r.Phase);
        Assert.Equal(0, maxDeviation, 3); // never leaves the water's surface
        Assert.Equal(route[^1], r.Position);
    }

    [Fact]
    public void ThePathEasesInAndMovesLegByLegAndSaysWhichWayToFace()
    {
        var p = new ReceiverPath(new[] { Vector3.Zero, new Vector3(0, 10, 0), new Vector3(100, 10, 0) }, new[] { 5f, 20f });
        Assert.Equal(110, p.Length, 3);
        Assert.Equal(0, p.LegAt(3)); Assert.Equal(1, p.LegAt(10.5f)); Assert.Equal(1, p.LegAt(500));
        Assert.Equal(new Vector3(0, 5, 0), p.PositionAt(5));
        Assert.Equal(new Vector3(50, 10, 0), p.PositionAt(60));
        Assert.Equal(new Vector3(100, 10, 0), p.PositionAt(999));
        Assert.Equal(new Vector2(1, 0), p.HeadingAt(2)); // on the way up it already faces the way it will fly
        Assert.Equal(new Vector2(1, 0), p.HeadingAt(60));
        Assert.Null(new ReceiverPath(new[] { Vector3.Zero, new Vector3(0, 10, 0) }, new[] { 5f }).HeadingAt(1));
        Assert.Throws<ArgumentException>(() => new ReceiverPath(new[] { Vector3.Zero }, Array.Empty<float>()));
    }

    // ---- the wire ----

    [Fact]
    public void TheReceiverEntityStateAndTheOwnerFieldRoundTrip()
    {
        var (r, book, entities) = Setup(ReceiverKind.Helicopter, min: 1);
        var s = r.Serialize();
        Assert.Equal((ReceiverKind.Helicopter, ReceiverPhase.Receiving), (ReceiverRules.KindOf(s.State), ReceiverRules.PhaseOf(s.State)));
        Assert.Equal(-7, s.Position.Z, 1);
        var d = Throw(book, entities, Party);
        r.Send();
        Assert.Equal(ReceiverPhase.Leaving, ReceiverRules.PhaseOf(r.Serialize().State));
        var ds = d.Serialize();
        Assert.Equal(DuffelState.Loaded, DuffelRules.StateOf(ds.State));
        Assert.True(ReceiverRules.IsReceiver(ds.Owner, out var id));
        Assert.Equal(r.Id, id);
        Assert.Equal(d.Slot, DuffelRules.SlotOf(ds.State));
        Assert.False(ReceiverRules.IsReceiver(VehicleRecord.FirstId + 5, out _)); // a vehicle's id is no Receiver
        Assert.False(ReceiverRules.IsReceiver(7, out _)); // nor a carrier's
        Assert.Equal(ReceiverRules.TypeId, WorldEntityRegistry.Shared.Find(ReceiverRules.TypeId) is { } def ? def.Id : (byte)0);
    }

    [Fact]
    public void TheReachEventRoundTripsAndRefusesGarbage()
    {
        var p = ReceiverCodec.EncodeReach(300, ReceiverKind.Boat, ReceiverAsk.NeedMore, 2, 4);
        Assert.True(ReceiverCodec.TryDecodeReach(p, out var id, out var kind, out var ask, out var on, out var min));
        Assert.Equal((300, ReceiverKind.Boat, ReceiverAsk.NeedMore, 2, 4), ((int)id, kind, ask, on, min));
        Assert.False(ReceiverCodec.TryDecodeReach(new byte[] { 1, 0, 9, 0, 0, 0 }, out _, out _, out _, out _, out _)); // no such kind
        Assert.False(ReceiverCodec.TryDecodeReach(new byte[] { 1, 0, 0, 9, 0, 0 }, out _, out _, out _, out _, out _)); // no such ask
        Assert.False(ReceiverCodec.TryDecodeReach(new byte[] { 1, 0 }, out _, out _, out _, out _, out _));
        Assert.Equal(51, (int)EventKind.ReceiverReach);
    }

    [Fact]
    public void TheDropWordsNameWhereTheVanGoesAndHowItIsFinished()
    {
        Assert.Equal(new DropText("the helicopter", "tell the pilot to leave"), ReceiverRules.Text(ReceiverKind.Helicopter));
        Assert.Equal(new DropText("the boat", "tell the boatman to leave"), ReceiverRules.Text(ReceiverKind.Boat));
        Assert.Equal(new DropText("the stash house", "hand over to the buyer"), ReceiverRules.Text(ReceiverKind.StashHouse));
    }

    // ---- the stash house's place ----

    static readonly Vector2[] Square = { new(0, 0), new(10, 0), new(10, 8), new(0, 8) };

    static HouseFloorPlan House(Vector2[] room, params PlanDoor[] doors) =>
        new("h", "P", 2.5f, 5.5f, Square, 2.5f, 5.5f, doors, new[] { new PlanRoom("r1", "main", "living", 70, room, doors.Select(d => d.Id).ToArray(), Array.Empty<string>(), Array.Empty<PlanLink>()) });

    static readonly PlanDoor SouthDoor = new("d1", HouseDoorKind.Hinged, new Vector2(4.5f, 0), new Vector2(5.5f, 0));

    [Fact]
    public void AStashHousesPalletStandsInsideBesideTheDoorNearestTheRoadWithTheBuyerClearOfTheWalls()
    {
        var house = House(new[] { new Vector2(0.15f, 0.15f), new Vector2(9.85f, 0.15f), new Vector2(9.85f, 7.85f), new Vector2(0.15f, 7.85f) }, SouthDoor);
        var def = Data.StashHouse;
        var spot = StashPlacement.Plan(house, new Vector2(5, 30), def.SizeM, def.Figure.Pos, _ => 0)!;
        Assert.True(spot.Inside);
        Assert.Equal("d1", spot.Door);
        Assert.Equal(house.FloorZ, spot.At.Y, 3); // on the house's floor
        var plan = new Vector2(spot.At.X, -spot.At.Z);
        Assert.True(PlanGeometry.Inside(Square, plan));
        Assert.True(MathF.Abs(plan.X - 5) > 0.5f + def.SizeM[0] / 2 - 0.01f, "beside the door, not in front of it"); // the doorway stays clear
        // the buyer, in the pallet's frame, is inside too
        var (s, c) = MathF.SinCos(spot.YawRad);
        var buyerG = new Vector2(spot.At.X + def.Figure.Pos[0] * c + def.Figure.Pos[2] * s, spot.At.Z - def.Figure.Pos[0] * s + def.Figure.Pos[2] * c);
        Assert.True(PlanGeometry.Inside(house.Rooms[0].Polygon, new Vector2(buyerG.X, -buyerG.Y)));
    }

    [Fact]
    public void WithoutRoomInsideThePalletGoesJustOutsideTheDoorOnTheGround()
    {
        var tiny = new[] { new Vector2(3.5f, 0.15f), new Vector2(6.5f, 0.15f), new Vector2(6.5f, 2.5f), new Vector2(3.5f, 2.5f) }; // a hall too small for the pallet beside the door
        var house = House(tiny, SouthDoor);
        var def = Data.StashHouse;
        var spot = StashPlacement.Plan(house, new Vector2(5, 30), def.SizeM, def.Figure.Pos, g => 1.25f)!;
        Assert.False(spot.Inside);
        Assert.Equal(1.25f, spot.At.Y, 3); // on the ground there
        Assert.False(PlanGeometry.Inside(Square, new Vector2(spot.At.X, -spot.At.Z))); // outside the walls
        Assert.True(Vector2.Distance(new Vector2(spot.At.X, -spot.At.Z), new Vector2(5, 0)) < 3.5f, "by the door");
    }

    [Fact]
    public void AHouseWithoutAnExteriorDoorHasNoStashSpot()
    {
        var def = Data.StashHouse;
        Assert.Null(StashPlacement.Plan(House(Square), new Vector2(5, 30), def.SizeM, def.Figure.Pos, _ => 0));
        var garage = new PlanDoor("g1", HouseDoorKind.Garage, new Vector2(3, 0), new Vector2(6, 0));
        Assert.Null(StashPlacement.Plan(House(Square, garage), new Vector2(5, 30), def.SizeM, def.Figure.Pos, _ => 0)); // a garage door is no front door
    }

    // ---- the map's zones ----

    static ZonePolygons Polygons(string kind, params float[][][] rings) =>
        ZonePolygons.Parse("{\"version\": 1, \"zones\": [{\"kind\": \"" + kind + "\", \"areas\": [\"Z-1\"], \"rings\": [" + string.Join(",", rings.Select(r => "[" + string.Join(",", r.Select(p => $"[{p[0]},{p[1]}]")) + "]")) + "]}]}");

    [Fact]
    public void AHelicopterPadIsItsMeanPointWithTheNoseTowardTheMiddleOfItsFirstEdge()
    {
        var pad = ReceiverSpot.Pad(new[] { new Vector2(10, 0), new Vector2(30, 0), new Vector2(30, 20), new Vector2(10, 20) })!;
        Assert.Equal(new Vector2(20, 10), pad.At);
        // the first edge is the one at z 0: north of the mean point is -z, so the nose points along -Z: a node's yaw 0
        Assert.Equal(0, pad.YawRad, 3);
        Assert.Empty(pad.Route);
        var east = ReceiverSpot.Pad(new[] { new Vector2(30, 0), new Vector2(30, 20), new Vector2(10, 20), new Vector2(10, 0) })!; // the first edge on the east side
        Assert.Equal(-MathF.PI / 2, east.YawRad, 3); // forward -Z turned to look along +X
        Assert.Null(ReceiverSpot.Pad(new[] { Vector2.Zero, Vector2.One }));
    }

    [Fact]
    public void ABoatMooringIsTheFirstPointWithTheBowTowardTheSecondAndTheRestIsTheRoute()
    {
        var m = ReceiverSpot.Mooring(new[] { new Vector2(0, 0), new Vector2(0, -10), new Vector2(40, -60), new Vector2(60, -60) })!;
        Assert.Equal(Vector2.Zero, m.At);
        Assert.Equal(0, m.YawRad, 3); // toward -z (north): forward
        Assert.Equal(new[] { new Vector2(0, -10), new Vector2(40, -60), new Vector2(60, -60) }, m.Route);
        Assert.Null(ReceiverSpot.Mooring(new[] { Vector2.Zero, Vector2.One }));
    }

    [Fact]
    public void TheMapReadsThePadsAndMooringsFromItsZonePolygonsAndTheJobsClaimThemApart()
    {
        var polygons = ZonePolygons.Parse("""
            {"version": 1, "zones": [
              {"kind": "cartel_heli", "areas": ["Z-1", "Z-2"], "rings": [[[0,0],[10,0],[10,10],[0,10]], [[500,0],[510,0],[510,10],[500,10]]]},
              {"kind": "cartel_boat", "areas": ["Z-3"], "rings": [[[900,0],[900,-10],[930,-50],[960,-50]]]}
            ]}
            """);
        var map = CartelMap.From(ZonePoints.Empty, null, null, polygons);
        Assert.Equal(2, map.Helis.Count);
        Assert.Single(map.Boats);
        Assert.Equal(new Vector3(505, 0, 5), map.HeliPoints[1]);
        Assert.Equal(new Vector3(900, 0, 0), map.BoatPoints[0]);
        Assert.Empty(CartelMap.Empty.Helis);
        // two Jobs never share a landing spot: the book holds it under its zone key
        var book = new Drycastle.ShooterCore.Jobs.JobBook();
        Assert.Equal("cartel_heli:1", CartelSpots.Key(CartelZones.HeliKind, 1));
        Assert.Equal("cartel_boat:0", CartelSpots.Key(CartelZones.BoatKind, 0));
        Assert.Empty(CartelSpots.Held(book, map));
    }
}

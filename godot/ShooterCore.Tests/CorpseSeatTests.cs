using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>ADR 0026: a dead pilot or driver stays seated, anyone else falls with the vehicle's velocity; a body that lands in water floats. Strict: an undeclared edge of Corpse.Flight throws (StateMachineTests.Init).</summary>
public class CorpseSeatTests
{
    static float? Floor(Vector3 from, Vector3 dir, float len)
    {
        if (dir.Y >= -0.5f) return null; // only the ground, at y = 0
        var d = from.Y / -dir.Y;
        return d >= 0 && d <= len ? d : null;
    }

    static float? Nothing(Vector3 from, Vector3 dir, float len) => null;

    /// <summary>A lake whose surface is at y = 2 between x = 10 and x = 20; the bed (the cast's ground) is at y = 0.</summary>
    static float? Lake(Vector3 p) => p.X is >= 10 and <= 20 ? 2f : null;

    static Corpse Body(Vector3 at) => new(1, at, 0, 0, 0);

    static void FlyDown(Corpse c, Func<Vector3, float?>? water = null, Func<Vector3, Vector3, float, float?>? cast = null)
    {
        for (var i = 0; i < 900 && c.Airborne; i++) c.Fly(1f / 30, cast ?? Floor, water);
    }

    // ---- drop (Grounded -> Airborne), steps 2 and 5 of the ticket ----------------------------------------------

    [Fact]
    public void DropMakesABodyFallFromAHoveringVehicleWhereLaunchOfZeroDoesNothing()
    {
        var c = Body(new Vector3(0, 30, 0));
        c.Launch(Vector3.Zero);
        Assert.False(c.Airborne);
        c.Throw(Vector3.Zero);
        Assert.True(c.Airborne);
        Assert.Equal(Vector3.Zero, c.Velocity);
        FlyDown(c);
        Assert.False(c.Airborne);
        Assert.InRange(c.Position.Y, -0.01f, 0.01f);
        Assert.InRange(c.Position.X, -0.01f, 0.01f); // straight down
    }

    [Fact]
    public void ThrowKeepsTheVehiclesVelocityAndCapsIt()
    {
        var c = Body(new Vector3(0, 10, 0));
        c.Throw(new Vector3(0, 0, -40)); // a helicopter at 40 m/s: the launch cap is 30
        Assert.True(c.Airborne);
        Assert.Equal(CorpseRules.MaxThrowSpeed, c.Velocity.Length(), 0.01f);
        Assert.True(c.Velocity.Z < 0);
    }

    [Fact]
    public void DropDoesNotDisturbABodyAlreadyInFlight()
    {
        var b = new ThrownBody(new Vector3(0, 10, 0));
        b.Launch(new Vector3(5, 0, 0));
        var v = b.Velocity;
        b.Drop();
        Assert.Equal(v, b.Velocity);
    }

    [Fact]
    public void ARunOutFlightSnapsToTheGroundUnderTheBodyNotMidAir()
    {
        var b = new ThrownBody(new Vector3(0, 200, 0));
        b.Launch(new Vector3(0, 30, 0)); // still rising when the cap (6 s) comes: it would not have landed
        for (var i = 0; i < 400 && b.Airborne; i++) b.Fly(1f / 30, Floor);
        Assert.False(b.Airborne);
        Assert.False(b.Floating);
        Assert.InRange(b.Position.Y, -0.01f, 0.01f);
    }

    [Fact]
    public void ARunOutFlightWithNoGroundBelowStaysWhereItIs()
    {
        var b = new ThrownBody(new Vector3(0, 400, 0));
        b.Launch(new Vector3(0, 30, 0));
        for (var i = 0; i < 400 && b.Airborne; i++) b.Fly(1f / 30, Nothing);
        Assert.False(b.Airborne);
        Assert.True(b.Position.Y > 100f);
    }

    // ---- water (Airborne -> Floating) ------------------------------------------------------------------------------

    [Fact]
    public void ABodyThatLandsInWaterFloatsAtTheSurface()
    {
        var c = Body(new Vector3(15, 20, 0));
        c.Throw(Vector3.Zero);
        FlyDown(c, Lake);
        Assert.False(c.Airborne);
        Assert.True(c.Floating);
        Assert.Equal(2f, c.Position.Y);
        Assert.Equal(15f, c.Position.X, 0.01f);
        Assert.Equal(Vector3.Zero, c.Velocity);
        Assert.Equal(0f, c.Serialize().PitchRad); // at rest on the wire: the client tells floating from the water under it
    }

    [Fact]
    public void ABodyThatLandsOnGroundBelowTheSurfaceFloatsToo()
    {
        // thrown from the bank at ground height into the lake: the first step already finds the body at or under the surface
        var c = Body(new Vector3(15, 1.5f, 0));
        c.Throw(Vector3.Zero);
        FlyDown(c, Lake);
        Assert.True(c.Floating);
        Assert.Equal(2f, c.Position.Y);
    }

    [Fact]
    public void ABodyLandingOnABridgeOverTheWaterLies()
    {
        float? Deck(Vector3 from, Vector3 dir, float len) => dir.Y < -0.5f && from.Y >= 4f && from.Y - 4f <= len ? from.Y - 4f : Floor(from, dir, len);
        var c = Body(new Vector3(15, 20, 0));
        c.Throw(Vector3.Zero);
        for (var i = 0; i < 900 && c.Airborne; i++) c.Fly(1f / 30, Deck, Lake);
        Assert.False(c.Airborne);
        Assert.False(c.Floating); // the deck is above the surface
        Assert.True(c.Position.Y >= 3.9f);
    }

    [Fact]
    public void ABodyOutsideTheLakeStillLandsOnTheGround()
    {
        var c = Body(new Vector3(30, 20, 0));
        c.Throw(Vector3.Zero);
        FlyDown(c, Lake);
        Assert.False(c.Floating);
        Assert.InRange(c.Position.Y, -0.01f, 0.01f);
    }

    [Fact]
    public void AFloatingBodyIsNotSteppedAndCanBeThrownAgain()
    {
        var c = Body(new Vector3(15, 20, 0));
        c.Throw(Vector3.Zero);
        FlyDown(c, Lake);
        var at = c.Position;
        c.Fly(1f, Floor, Lake); // not airborne: nothing happens
        Assert.Equal(at, c.Position);
        c.Throw(new Vector3(3, 6, 0)); // a Floating -> Airborne edge
        Assert.True(c.Airborne);
        FlyDown(c, Lake);
        Assert.True(c.Floating);
    }

    [Fact]
    public void AFloatingBodyIsResetToGroundedWhenADummyStandsUp()
    {
        var b = new ThrownBody(new Vector3(15, 20, 0));
        b.Launch(new Vector3(0.1f, 0, 0));
        for (var i = 0; i < 900 && b.Airborne; i++) b.Fly(1f / 30, Floor, Lake);
        Assert.True(b.Floating);
        b.Reset(new Vector3(1, 0, 1));
        Assert.False(b.Floating);
        Assert.False(b.Airborne);
    }

    // ---- seated (Grounded -> Seated -> Airborne / Grounded) -------------------------------------------------------

    [Fact]
    public void ASeatedBodyFollowsTheVehicleAndIsNotFlown()
    {
        var c = Body(new Vector3(0, 30, 0));
        c.Attach(new Vector3(0, 30, 0));
        Assert.True(c.Seated);
        Assert.False(c.Airborne);
        c.Fly(1f, Floor); // not airborne: nothing happens
        c.Follow(new Vector3(5, 31, -2));
        Assert.Equal(new Vector3(5, 31, -2), c.Position);
        Assert.Equal(0f, c.Serialize().PitchRad);
    }

    [Fact]
    public void FollowAndAttachDoNothingOnABodyThatIsNotAtRestOrNotSeated()
    {
        var c = Body(new Vector3(0, 10, 0));
        c.Follow(new Vector3(9, 9, 9));
        Assert.Equal(new Vector3(0, 10, 0), c.Position);
        c.Throw(Vector3.Zero);
        c.Attach(new Vector3(1, 1, 1)); // airborne: cannot sit down
        Assert.False(c.Seated);
        Assert.True(c.Airborne);
    }

    [Fact]
    public void ASeatedBodyDropsAfterTheVehicleHasRestedForASecond()
    {
        var c = Body(new Vector3(0, 10, 0));
        c.Attach(new Vector3(0, 10, 0));
        Assert.False(c.TickSeated(0.5f, false));
        Assert.False(c.TickSeated(0.9f, true));
        Assert.True(c.Seated);
        Assert.True(c.TickSeated(0.2f, true)); // 1.1 s at rest
        Assert.False(c.Seated);
        Assert.True(c.Airborne);
        Assert.Equal(Vector3.Zero, c.Velocity);
        FlyDown(c);
        Assert.InRange(c.Position.Y, -0.01f, 0.01f);
        Assert.False(c.TickSeated(5f, true)); // no longer seated: nothing
    }

    [Fact]
    public void MovementStartsTheRestCountOver()
    {
        var c = Body(new Vector3(0, 10, 0));
        c.Attach(new Vector3(0, 10, 0));
        Assert.False(c.TickSeated(0.9f, true));
        Assert.False(c.TickSeated(0.1f, false)); // moved: reset
        Assert.False(c.TickSeated(0.9f, true));
        Assert.True(c.Seated);
        Assert.True(c.TickSeated(0.2f, true));
    }

    [Fact]
    public void ASeatedBodyCanBeThrownWithADestroyedVehiclesVelocity()
    {
        var c = Body(new Vector3(0, 10, 0));
        c.Attach(new Vector3(0, 10, 0));
        c.Throw(new Vector3(8, 2, 0));
        Assert.False(c.Seated);
        Assert.True(c.Airborne);
        Assert.Equal(8f, c.Velocity.X, 0.01f);
        var d = Body(new Vector3(0, 10, 0));
        d.Attach(new Vector3(0, 10, 0));
        d.Throw(Vector3.Zero); // a wreck at rest: still falls
        Assert.True(d.Airborne);
    }

    [Fact]
    public void ASeatedBodyResetsToGroundedForADummy()
    {
        var b = new ThrownBody(Vector3.Zero);
        b.Attach(new Vector3(0, 3, 0));
        b.Reset(new Vector3(4, 0, 4));
        Assert.False(b.Seated);
        Assert.Equal(new Vector3(4, 0, 4), b.Position);
    }

    // ---- looting ---------------------------------------------------------------------------------------------------

    static (CorpseBook book, ushort id, Corpse body) SeatedCorpse()
    {
        var types = new WorldEntityRegistry();
        CorpseRules.Register(types);
        var book = new CorpseBook(new WorldEntityManager(types));
        var body = Body(new Vector3(0, 1, 0));
        body.Attach(new Vector3(0, 1, 0));
        return (book, book.Add(body)!.Value, body);
    }

    static readonly GameData Shipped = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data"));

    [Fact]
    public void ASeatedCorpseCannotBeLootedWhileTheVehicleMovesButCanOnceItStops()
    {
        var (book, id, body) = SeatedCorpse();
        var taker = new PlayerWeapons(Shipped.Weapons, null, LoadoutHotbar.Build(Shipped.Weapons, new Loadout(0, "ak47", "glock17", "", "", "", "")));
        Assert.False(body.CanLoot);
        Assert.Equal(PickUpResult.OutOfReach, book.TryPickUp(id, new Vector3(1, 1, 0), taker, Shipped.Weapons, CorpseRules.PrimaryPos));
        body.TickSeated(0.1f, true); // at rest: the second before it drops has begun
        Assert.True(body.CanLoot);
        body.TickSeated(2f, true); // dropped
        Assert.True(body.CanLoot);
    }

    // ---- seats -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ABlockedSeatCannotBeTakenOrSwitchedToButDoesNotMakeTheVehicleOccupied()
    {
        var s = new VehicleSeat(3);
        s.Block(0);
        Assert.True(s.IsBlocked(0));
        Assert.False(s.Occupied);
        Assert.False(s.IsFree(0));
        Assert.True(s.HasFree);
        Assert.False(s.TryEnter(5, 0));
        Assert.True(s.TryEnter(5, 1));
        Assert.False(s.TrySwitch(5, 0));
        Assert.True(s.TrySwitch(5, 2));
        s.Exit(5);
        Assert.True(s.TryEnter(6)); // the first free seat is not the blocked one
        Assert.Equal(1, s.SeatOf(6));
        s.Unblock(0);
        Assert.True(s.IsFree(0));
        Assert.True(s.TrySwitch(6, 0));
    }

    [Fact]
    public void WithTheDriversSeatBlockedAnEmptyVehicleGivesTheNearestOtherSeat()
    {
        var def = new VehicleDef();
        def.Seats.Clear();
        def.Seats.Add(new VehicleSeatDef { Role = "driver", Offset = new[] { 0f, 0f, 0f } });
        def.Seats.Add(new VehicleSeatDef { Role = "passenger", Offset = new[] { 5f, 0f, 0f } });
        def.Seats.Add(new VehicleSeatDef { Role = "passenger", Offset = new[] { -5f, 0f, 0f } });
        var s = new VehicleSeat(3);
        Assert.Equal(0, s.NearestFreeSeat(def, Vector3.Zero, 0, new Vector3(100, 0, 0), float.MaxValue)); // empty: the driver, wherever they stand
        s.Block(0);
        Assert.Equal(1, s.NearestFreeSeat(def, Vector3.Zero, 0, new Vector3(100, 0, 0), float.MaxValue));
        s.Block(1); s.Block(2);
        Assert.False(s.HasFree);
        Assert.Equal(-1, s.NearestFreeSeat(def, Vector3.Zero, 0, Vector3.Zero, float.MaxValue));
    }

    [Fact]
    public void ClearingASeatMapUnblocksIt()
    {
        var s = new VehicleSeat(2);
        s.Block(0);
        s.Clear();
        Assert.False(s.IsBlocked(0));
    }

    // ---- boarding kicks the corpse out (#330) ----------------------------------------------------------------------

    [Fact]
    public void BoardingASeatThatHoldsACorpseTakesItAtOnceAndSaysTheCorpseIsKicked()
    {
        var s = new VehicleSeat(2);
        s.Block(0);
        Assert.True(s.HoldsCorpse(0));
        Assert.False(s.HoldsCorpse(1));
        Assert.True(s.TryBoard(5, 0, out var kicked));
        Assert.True(kicked);
        Assert.Equal(0, s.SeatOf(5));
        Assert.False(s.IsBlocked(0));
        Assert.False(s.HoldsCorpse(0));
    }

    [Fact]
    public void BoardingAFreeSeatKicksNothingAndATakenOrBadSeatChangesNothing()
    {
        var s = new VehicleSeat(2);
        Assert.True(s.TryBoard(5, 1, out var kicked));
        Assert.False(kicked);
        Assert.False(s.TryBoard(6, 1, out _)); // taken
        Assert.False(s.TryBoard(5, 0, out _)); // already seated
        Assert.False(s.TryBoard(6, 2, out _)); // no such seat
        Assert.False(s.TryBoard(0, 0, out _));
        Assert.Equal(0, s.Occupant(0));
    }

    [Fact]
    public void AVehicleWithOnlyCorpseSeatsIsBoardableAndTheNearestSeatCountsCorpseSeatsWhenKicking()
    {
        var def = new VehicleDef();
        def.Seats.Clear();
        def.Seats.Add(new VehicleSeatDef { Role = "driver", Offset = new[] { 0f, 0f, 0f } });
        def.Seats.Add(new VehicleSeatDef { Role = "passenger", Offset = new[] { 5f, 0f, 0f } });
        var s = new VehicleSeat(2);
        s.Block(0); s.Block(1);
        Assert.False(s.HasFree);
        Assert.True(s.HasBoardable);
        Assert.Equal(-1, s.NearestFreeSeat(def, Vector3.Zero, 0, Vector3.Zero, float.MaxValue));
        Assert.Equal(0, s.NearestFreeSeat(def, Vector3.Zero, 0, new Vector3(100, 0, 0), float.MaxValue, kickCorpses: true)); // nobody aboard: the driver's seat
        Assert.False(s.TryEnter(7, 0)); // TryEnter still refuses a corpse seat
        Assert.True(s.TryBoard(7, 0, out _));
        Assert.Equal(1, s.NearestFreeSeat(def, Vector3.Zero, 0, Vector3.Zero, float.MaxValue, kickCorpses: true)); // the passenger corpse is next
    }

    [Fact]
    public void AKickedCorpseFliesOutTheBoardersSideWithAHopAndLandsGroundedWithItsItems()
    {
        // yaw 0: the vehicle's right is +X
        var v = CorpseRules.ThrowFromSeat(Vector3.Zero, 0, new Vector3(-2, 0, 0), Vector3.Zero);
        Assert.Equal(-CorpseRules.KickSpeed, v.X, 0.001f);
        Assert.Equal(CorpseRules.KickLift, v.Y, 0.001f);
        Assert.Equal(0, v.Z, 0.001f);
        Assert.True(CorpseRules.ThrowFromSeat(Vector3.Zero, 0, new Vector3(2, 0, 0), Vector3.Zero).X > 0);
        var turned = CorpseRules.ThrowFromSeat(Vector3.Zero, MathF.PI / 2, new Vector3(0, 0, -2), Vector3.Zero); // yaw 90: right is -Z, the boarder stands on it
        Assert.Equal(-CorpseRules.KickSpeed, turned.Z, 0.001f);
        var moving = CorpseRules.ThrowFromSeat(Vector3.Zero, 0, new Vector3(2, 0, 0), new Vector3(0, 0, -3));
        Assert.Equal(-3, moving.Z, 0.001f); // the vehicle's velocity adds

        var item = new ItemStack(1);
        var c = new Corpse(1, new Vector3(0, 0.5f, 0), 0, 0, 0, new List<ItemStack> { item });
        c.Attach(new Vector3(0, 1, 0));
        Assert.True(c.Seated);
        c.Throw(v);
        Assert.False(c.Seated);
        Assert.True(c.Airborne); // Corpse.Flight Seated -> Airborne
        for (var i = 0; i < 300 && c.Airborne; i++) c.Fly(1f / 30, Floor);
        Assert.False(c.Airborne);
        Assert.True(c.Position.X < -1f); // out the boarder's side
        Assert.Single(c.Items);
        Assert.True(c.CanLoot);
    }
}

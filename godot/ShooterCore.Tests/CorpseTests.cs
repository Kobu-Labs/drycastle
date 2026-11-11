using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Ticket #130: corpses, the pickup swap rules, the 16-corpse cap, expiry and the corpse payload codecs.</summary>
public class CorpseTests
{
    [Fact]
    public void CorpseEventKindsDoNotCollideWithKillstreakEvents()
    {
        // #120 owns 17-19 (StreakEarned/Used/Ended); corpse events sit after them on the wire.
        Assert.Equal(20, (int)EventKind.PickUpCorpse);
        Assert.Equal(21, (int)EventKind.Hotbar);
        Assert.True((int)EventKind.PickUpCorpse > (int)EventKind.StreakEnded);
    }

    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);
    static WeaponsFile File => Shipped.Weapons;

    static int IndexOf(string id) => File.Weapons.FindIndex(w => w.Id == id);

    static (WorldEntityManager world, CorpseBook book, List<WorldDespawned> despawns) Setup()
    {
        var types = new WorldEntityRegistry();
        CorpseRules.Register(types);
        var world = new WorldEntityManager(types);
        var despawns = new List<WorldDespawned>();
        world.Despawned += despawns.Add;
        return (world, new CorpseBook(world), despawns);
    }

    /// <summary>A taker holding a primary (ak47) and a secondary (glock17), knife after, as LoadoutHotbar builds it.</summary>
    static PlayerWeapons Taker(string primary = "ak47", string secondary = "glock17") =>
        new(File, null, LoadoutHotbar.Build(File, new Loadout(0, primary, secondary, "", "", "", "")));

    static Corpse CorpseOf(string weapon, Vector3 at, int rounds = 60, ushort owner = 7) =>
        new(owner, at, 0f, IndexOf(weapon), rounds);

    // ---- reach and slot rules --------------------------------------------------------------------------------------

    [Fact]
    public void ReachIsTwoAndAHalfMetresPlusTheLatencySlack()
    {
        var corpse = Vector3.Zero;
        Assert.True(CorpseRules.InReach(new Vector3(2.5f, 0, 0), corpse));
        Assert.False(CorpseRules.InReach(new Vector3(2.6f, 0, 0), corpse));
        Assert.True(CorpseRules.InReach(new Vector3(2.9f, 0, 0), corpse, CorpseRules.LatencySlackMetres));
    }

    [Fact]
    public void APrimaryReplacesThePrimaryOrTheSecondaryButAPistolOnlyTheSecondary()
    {
        var ak = File.Weapons[IndexOf("ak47")];
        var pistol = File.Weapons[IndexOf("glock17")];
        var knife = File.Weapons[IndexOf("knife")];
        Assert.True(CorpseRules.CanReplace(ak, CorpseRules.PrimaryPos));
        Assert.True(CorpseRules.CanReplace(ak, CorpseRules.SecondaryPos));
        Assert.False(CorpseRules.CanReplace(pistol, CorpseRules.PrimaryPos));
        Assert.True(CorpseRules.CanReplace(pistol, CorpseRules.SecondaryPos));
        Assert.False(CorpseRules.CanReplace(knife, CorpseRules.PrimaryPos));
        Assert.False(CorpseRules.CanReplace(knife, CorpseRules.SecondaryPos));
    }

    // ---- pickup -----------------------------------------------------------------------------------------------------

    [Fact]
    public void PickUpPutsTheCorpseWeaponInThePrimarySlotWithAFreshMagazineAndHalfTheReserve()
    {
        var (world, book, _) = Setup();
        var id = book.Add(CorpseOf("m4a1", Vector3.Zero))!.Value;
        var taker = Taker();
        var result = book.TryPickUp(id, new Vector3(1, 0, 0), taker, File, CorpseRules.PrimaryPos);
        Assert.Equal(PickUpResult.Ok, result);
        Assert.Equal(IndexOf("m4a1"), taker.HotbarWeapon(CorpseRules.PrimaryPos));
        Assert.Equal(File.Weapons[IndexOf("m4a1")].MagazineSize + File.Weapons[IndexOf("m4a1")].ReserveAmmo / 2, taker.RoundsOf(IndexOf("m4a1")));
    }

    [Fact]
    public void TheReplacedWeaponIsDroppedOnTheCorpseSoItCanBeSwappedBack()
    {
        var (world, book, _) = Setup();
        var id = book.Add(CorpseOf("m4a1", Vector3.Zero))!.Value;
        var taker = Taker(secondary: "glock17");
        Assert.Equal(PickUpResult.Ok, book.TryPickUp(id, Vector3.Zero, taker, File, CorpseRules.SecondaryPos));

        // the taker's secondary is now the m4a1; the glock it gave up is on the corpse and can go back into the secondary position
        Assert.Equal(IndexOf("m4a1"), taker.HotbarWeapon(CorpseRules.SecondaryPos));
        Assert.Equal(PickUpResult.Ok, book.TryPickUp(id, Vector3.Zero, taker, File, CorpseRules.SecondaryPos));
        Assert.Equal(IndexOf("glock17"), taker.HotbarWeapon(CorpseRules.SecondaryPos));
    }

    [Fact]
    public void TheCorpseCarriesTheDroppedWeaponAndItsRounds()
    {
        var (world, book, _) = Setup();
        var corpse = CorpseOf("m4a1", Vector3.Zero);
        var id = book.Add(corpse)!.Value;
        var taker = Taker(secondary: "glock17");
        var glockRounds = taker.RoundsOf(IndexOf("glock17"));
        Assert.Equal(PickUpResult.Ok, book.TryPickUp(id, Vector3.Zero, taker, File, CorpseRules.SecondaryPos));
        Assert.Equal(IndexOf("glock17"), corpse.Weapon);
        Assert.Equal(glockRounds, corpse.Rounds);
        // the wire state the clients read: weapon in State, rounds in Hp
        var s = corpse.Serialize();
        Assert.Equal(IndexOf("glock17"), s.State);
        Assert.Equal(glockRounds, s.Hp);
    }

    [Fact]
    public void ARejectedPickUpChangesNothing()
    {
        var (world, book, _) = Setup();
        var id = book.Add(CorpseOf("m4a1", Vector3.Zero))!.Value;
        var taker = Taker();
        var before = taker.HotbarWeapon(CorpseRules.PrimaryPos);

        Assert.Equal(PickUpResult.OutOfReach, book.TryPickUp(id, new Vector3(10, 0, 0), taker, File, CorpseRules.PrimaryPos));
        Assert.Equal(PickUpResult.BadSlot, book.TryPickUp(id, Vector3.Zero, taker, File, 3));
        Assert.Equal(PickUpResult.Gone, book.TryPickUp(9999, Vector3.Zero, taker, File, CorpseRules.PrimaryPos));
        Assert.Equal(before, taker.HotbarWeapon(CorpseRules.PrimaryPos));
    }

    [Fact]
    public void APistolCorpseCannotGoIntoThePrimarySlot()
    {
        var (world, book, _) = Setup();
        var id = book.Add(CorpseOf("deagle", Vector3.Zero))!.Value;
        var taker = Taker();
        Assert.Equal(PickUpResult.WrongKind, book.TryPickUp(id, Vector3.Zero, taker, File, CorpseRules.PrimaryPos));
        Assert.Equal(PickUpResult.Ok, book.TryPickUp(id, Vector3.Zero, taker, File, CorpseRules.SecondaryPos));
    }

    [Fact]
    public void AWeaponAlreadyOnTheHotbarCannotBeTakenTwice()
    {
        var (world, book, _) = Setup();
        var id = book.Add(CorpseOf("ak47", Vector3.Zero))!.Value;
        var taker = Taker(primary: "ak47");
        Assert.Equal(PickUpResult.AlreadyHeld, book.TryPickUp(id, Vector3.Zero, taker, File, CorpseRules.PrimaryPos));
    }

    [Fact]
    public void ReplacingTheSelectedWeaponHandsTheSelectionToTheNewOne()
    {
        var (world, book, _) = Setup();
        var id = book.Add(CorpseOf("m4a1", Vector3.Zero))!.Value;
        var taker = Taker();
        taker.Update(0, new WeaponInput(false, false, IndexOf("ak47") + 1, false));
        Assert.Equal(PickUpResult.Ok, book.TryPickUp(id, Vector3.Zero, taker, File, CorpseRules.PrimaryPos));
        Assert.Equal(IndexOf("m4a1") + 1, taker.Slot);
    }

    // ---- cap, expiry, round end --------------------------------------------------------------------------------------

    [Fact]
    public void TheSeventeenthCorpseRemovesTheOldestOne()
    {
        var (world, book, despawns) = Setup();
        var ids = new List<ushort>();
        for (var i = 0; i < CorpseRules.MaxCorpses; i++) ids.Add(book.Add(CorpseOf("ak47", new Vector3(i, 0, 0)))!.Value);
        Assert.Equal(CorpseRules.MaxCorpses, book.Count);

        var newest = book.Add(CorpseOf("ak47", new Vector3(99, 0, 0)));
        Assert.NotNull(newest);
        Assert.Equal(CorpseRules.MaxCorpses, book.Count);
        Assert.Equal(CorpseRules.MaxCorpses, world.Count);
        var gone = Assert.Single(despawns);
        Assert.Equal(ids[0], gone.Id);
        Assert.Equal(WorldDespawnReason.Removed, gone.Reason);
    }

    [Fact]
    public void ACorpseExpiresAfterTwoMinutes()
    {
        var (world, book, despawns) = Setup();
        book.Add(CorpseOf("ak47", Vector3.Zero));
        world.Tick(CorpseRules.LifetimeSeconds - 1f);
        Assert.Equal(1, book.Count);
        world.Tick(1.5f);
        Assert.Equal(0, book.Count);
        Assert.Equal(WorldDespawnReason.Expired, Assert.Single(despawns).Reason);
    }

    [Fact]
    public void ARoundEndClearsTheCorpses()
    {
        var (world, book, _) = Setup();
        book.Add(CorpseOf("ak47", Vector3.Zero));
        book.Add(CorpseOf("sniper", Vector3.Zero));
        world.Clear();
        Assert.Equal(0, book.Count);
        Assert.Equal(0, world.Count);
    }

    [Fact]
    public void CorpsesTakeNoDamage()
    {
        var (world, book, _) = Setup();
        var id = book.Add(CorpseOf("ak47", Vector3.Zero))!.Value;
        world.Damage(id, 500, 3);
        Assert.Equal(1, book.Count);
    }

    // ---- codecs -----------------------------------------------------------------------------------------------------

    [Fact]
    public void PickUpPayloadRoundTrips()
    {
        var bytes = CorpseCodec.EncodePickUp(513, 1);
        Assert.True(CorpseCodec.TryDecodePickUp(bytes, out var id, out var pos));
        Assert.Equal((ushort)513, id);
        Assert.Equal(1, pos);
        Assert.False(CorpseCodec.TryDecodePickUp(new byte[2], out _, out _));
    }

    [Fact]
    public void HotbarPayloadRoundTripsAndRejectsBadLengths()
    {
        var bytes = CorpseCodec.EncodeHotbar(new[] { 5, 9, 2 });
        Assert.True(CorpseCodec.TryDecodeHotbar(bytes, out var weapons));
        Assert.Equal(new[] { 5, 9, 2 }, weapons);
        Assert.False(CorpseCodec.TryDecodeHotbar(new byte[] { 3, 1 }, out _));
    }
}

/// <summary>The ragdoll: a body killed by a blast or a car is thrown, flies under gravity and lands.</summary>
public class CorpseThrowTests
{
    static float? Floor(Vector3 from, Vector3 dir, float len)
    {
        if (dir.Y >= -0.5f) return null;                // only the ground, at y = 0
        var d = from.Y / -dir.Y;
        return d >= 0 && d <= len ? d : null;
    }

    [Fact]
    public void ThrownBodyFliesAwayAndLands()
    {
        var c = new Corpse(1, Vector3.Zero, 0, 0, 0);
        c.Launch(CorpseRules.ThrowFromBlast(new Vector3(4.5f, 0, 0)));
        Assert.True(c.Airborne);
        for (var i = 0; i < 600 && c.Airborne; i++) c.Fly(1f / 30, Floor);
        Assert.False(c.Airborne);
        Assert.True(c.Position.X > 5f, $"thrown {c.Position.X:F1} m");
        Assert.InRange(c.Position.Y, -0.01f, 0.01f);
        Assert.Equal(Vector3.Zero, c.Velocity);
    }

    [Fact]
    public void NoPushLeavesTheBodyWhereItFell()
    {
        var c = new Corpse(1, new Vector3(3, 0, 3), 0, 0, 0);
        c.Launch(Vector3.Zero);
        Assert.False(c.Airborne);
        Assert.Equal(0f, c.Serialize().PitchRad);
    }

    [Fact]
    public void ABodyInFlightIsFlaggedInItsState()
    {
        var c = new Corpse(1, Vector3.Zero, 0, 0, 0);
        c.Launch(new Vector3(5, 0, 0));
        Assert.True(c.Serialize().PitchRad > CorpseRules.FlightPitch * 0.5f);
    }

    [Fact]
    public void AWallStopsTheBodyAndItDropsStraightDown()
    {
        float? Wall(Vector3 from, Vector3 dir, float len) => dir.X > 0.5f && 4f - from.X is var d && d >= 0 && d <= len ? d : Floor(from, dir, len);
        var c = new Corpse(1, Vector3.Zero, 0, 0, 0);
        c.Launch(new Vector3(20, 3, 0));
        for (var i = 0; i < 600 && c.Airborne; i++) c.Fly(1f / 30, Wall);
        Assert.InRange(c.Position.X, 3f, 4f);
    }

    [Fact]
    public void RoadkillIsThrownAheadOfTheCar()
    {
        var v = CorpseRules.ThrowFromCar(new Vector3(0, 0, -1), 15f);
        Assert.True(v.Z < -10f && v.Y > 0);
    }

    [Fact]
    public void ThrowSpeedIsCapped()
    {
        var c = new Corpse(1, Vector3.Zero, 0, 0, 0);
        c.Launch(new Vector3(500, 0, 0));
        Assert.True(c.Velocity.Length() <= CorpseRules.MaxThrowSpeed + 0.01f);
    }
}

public class DummyThrowTests
{
    [Fact]
    public void AThrownDummyFliesAndStandsBackOnItsSpotWhenItRespawns()
    {
        var spot = new TargetSpot(new Vector3(10, 0, 10), 0, Stance.Stand, 10);
        var dummies = new TargetDummies(new[] { spot }, 3f);
        var d = dummies.All[0];
        Assert.Equal(spot.Feet, d.Body.Position);
        Assert.Equal(DamageResult.Killed, dummies.Damage(d.Id, 1000, 0));
        d.Body.Launch(new Vector3(8, 4, 0));
        for (var i = 0; i < 300 && d.Body.Airborne; i++) d.Body.Fly(1f / 30, (from, dir, len) => dir.Y < -0.5f && from.Y / -dir.Y <= len ? from.Y / -dir.Y : null);
        Assert.False(d.Body.Airborne);
        Assert.True(d.Body.Position.X > spot.Feet.X + 3f);
        Assert.Contains(d.Id, dummies.Update(10));
        Assert.Equal(spot.Feet, d.Body.Position);
    }
}

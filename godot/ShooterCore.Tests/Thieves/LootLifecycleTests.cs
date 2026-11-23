using System.Numerics;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Thieves;
using Xunit;
using static Drycastle.ShooterCore.Tests.Thieves.LootTests;

namespace Drycastle.ShooterCore.Tests.Thieves;

/// <summary>The Loot's explicit machines (Loot.Piece, Loot.ReturnHold), a Raid's end announced once, and what a hit or a flight does to Loot a Thief carries. State machines run Strict.</summary>
public class LootLifecycleTests
{
    // ---- Loot.ReturnHold ----

    static bool Hold(ref LootReturnHold h, bool use, ushort? reach, double dt = 0.25, double seconds = 1) => h.Tick(use, reach, dt, seconds);

    [Fact]
    public void TheReturnHoldCountsInReachCompletesOnceAndNeedsANewPressAfterAHit()
    {
        var h = new LootReturnHold();
        Assert.Equal(LootHoldState.Idle, h.State);
        Assert.False(Hold(ref h, true, 5));
        Assert.Equal(LootHoldState.Holding, h.State);
        Assert.Equal((0.25, (ushort)5), (h.Held, h.Target));
        Assert.False(Hold(ref h, true, 5)); Assert.False(Hold(ref h, true, 5));
        Assert.Equal(0.75, h.Held, 6);
        Assert.False(Hold(ref h, true, 6));   // other Loot: starts over on it
        Assert.Equal((0.25, (ushort)6), (h.Held, h.Target));
        Assert.False(Hold(ref h, true, null)); // out of reach: starts over, still pressing
        Assert.Equal((0.0, (ushort)0, LootHoldState.Idle), (h.Held, h.Target, h.State));
        for (var i = 0; i < 3; i++) Assert.False(Hold(ref h, true, 6));
        Assert.True(Hold(ref h, true, 6));     // the fourth 0.25 completes
        Assert.Equal((0.0, (ushort)0, LootHoldState.Spent), (h.Held, h.Target, h.State));
        Assert.False(Hold(ref h, true, 6));    // once per hold
        Assert.False(Hold(ref h, true, null)); // leaving reach does not undo it
        Assert.Equal(LootHoldState.Spent, h.State);
        Assert.False(Hold(ref h, false, 6));   // released
        Assert.Equal(LootHoldState.Idle, h.State);
        // a hit mid-hold: over until the key is pressed again
        Hold(ref h, true, 6); Hold(ref h, true, 6);
        h.Interrupt();
        Assert.Equal((0.0, (ushort)0, LootHoldState.Spent), (h.Held, h.Target, h.State));
        for (var i = 0; i < 10; i++) Assert.False(Hold(ref h, true, 6));
        Hold(ref h, false, null);
        for (var i = 0; i < 3; i++) Assert.False(Hold(ref h, true, 6));
        Assert.True(Hold(ref h, true, 6));
        // a hit with the key up (the server calls Interrupt before the tick of the same input): the press of that input does nothing
        Hold(ref h, false, null);
        h.Interrupt();
        Assert.Equal(LootHoldState.Spent, h.State);
        Assert.False(Hold(ref h, true, 6));
        Assert.Equal(0, h.Held);
        h.Reset();
        Assert.Equal(LootHoldState.Idle, h.State);
        Assert.False(Hold(ref h, true, 6));
        Assert.Equal(0.25, h.Held);
        h.Reset();
        Assert.Equal((0.0, (ushort)0, LootHoldState.Idle), (h.Held, h.Target, h.State));
    }

    [Fact]
    public void EveryEdgeOfTheReturnHoldIsDeclaredAndTheSpecIsWhatTheCodeDoes()
    {
        var spec = LootReturnHold.Spec;
        Assert.Equal("Loot.ReturnHold", spec.Name);
        Assert.Empty(spec.Unreachable);
        // walk every declared edge through the real struct (Strict: an undeclared one throws)
        foreach (var (from, to) in new[]
        {
            (LootHoldState.Idle, LootHoldState.Holding), (LootHoldState.Holding, LootHoldState.Idle), (LootHoldState.Holding, LootHoldState.Spent),
            (LootHoldState.Spent, LootHoldState.Idle), (LootHoldState.Idle, LootHoldState.Spent),
        })
        {
            var h = Reach(from);
            Assert.Equal(from, h.State);
            switch ((from, to))
            {
                case (LootHoldState.Idle, LootHoldState.Holding): h.Tick(true, 1, 0.1, 1); break;
                case (LootHoldState.Holding, LootHoldState.Idle): h.Tick(true, null, 0.1, 1); break;
                case (LootHoldState.Holding, LootHoldState.Spent): h.Tick(true, 1, 2, 1); break;
                case (LootHoldState.Spent, LootHoldState.Idle): h.Tick(false, null, 0.1, 1); break;
                case (LootHoldState.Idle, LootHoldState.Spent): h.Interrupt(); break;
            }
            Assert.Equal(to, h.State);
        }
        Assert.Equal(5, spec.TransitionNames.Count); // nothing declared that these do not walk
        Assert.Equal(new[] { LootHoldState.Idle, LootHoldState.Holding, LootHoldState.Spent }, Enum.GetValues<LootHoldState>());
    }

    static LootReturnHold Reach(LootHoldState s)
    {
        var h = new LootReturnHold();
        switch (s)
        {
            case LootHoldState.Holding: h.Tick(true, 1, 0.1, 1); break;
            case LootHoldState.Spent: h.Tick(true, 1, 0.1, 1); h.Interrupt(); break;
        }
        return h;
    }

    // ---- Loot.Piece ----

    sealed class Walker(LootBook book)
    {
        public readonly HashSet<(LootPieceState, LootPieceState)> Edges = new();

        /// <summary>Does <paramref name="act"/> to the piece and notes the edge it took.</summary>
        public void Do(LootOrigin piece, LootPieceState expectBefore, LootPieceState expectAfter, Action act)
        {
            var before = book.Pieces.Of(piece);
            Assert.Equal(expectBefore, before);
            act();
            var after = book.Pieces.Of(piece);
            Assert.Equal(expectAfter, after);
            if (before != after) Edges.Add((before, after));
        }
    }

    static LootOrigin OriginOf(StealTarget t) => new(t.Kind, t.Id);

    [Fact]
    public void ThePieceLifecycleIsWalkedThroughTheRealApisOverEveryDeclaredEdge()
    {
        const LootPieceState AtPlace = LootPieceState.AtPlace, Claimed = LootPieceState.Claimed, Carried = LootPieceState.Carried, Lying = LootPieceState.Lying,
            LyingClaimed = LootPieceState.LyingClaimed, Loaded = LootPieceState.Loaded;
        var rig = new Rig(f => f.Gang.Raid = new GangRaidCounts { GangBosses = 1, Enforcers = 0, Thieves = 0 }); // a crew that never steals
        rig.Cars.Add(0x4001, 30, 12);
        var raid = rig.Launch();
        rig.RunUntil(() => raid.Van.State == RaidVanState.Opening);
        var w = new Walker(rig.Book);
        var spot = new Vector3(1, 0, 1);
        StealTarget t = default;
        LootOrigin o = default;

        // at its place -> claimed -> (given up) at its place -> claimed -> carried -> lying
        t = rig.Book.Pick(spot, 90, spot)!.Value; o = OriginOf(t);
        Assert.Equal(Claimed, rig.Book.Pieces.Of(o));
        w.Edges.Add((AtPlace, Claimed));
        w.Do(o, Claimed, AtPlace, () => rig.Book.Release(t));
        Assert.Equal(0, rig.Registry.Claimed);
        w.Do(o, AtPlace, Claimed, () => Assert.True(rig.Book.Claim(t)));
        w.Do(o, Claimed, Carried, () => Assert.Equal(o, rig.Book.Take(t)));
        w.Do(o, Carried, Lying, () => rig.Book.Drop(o, spot));
        // lying -> claimed there -> let go -> claimed -> taken up -> loaded -> the van is destroyed: back at its place
        StealTarget lt = default;
        w.Do(o, Lying, LyingClaimed, () => { lt = rig.Book.Pick(spot, 90, spot)!.Value; Assert.True(lt.Lying); });
        w.Do(o, LyingClaimed, Lying, () => rig.Book.Release(lt));
        w.Do(o, Lying, LyingClaimed, () => Assert.True(rig.Book.Claim(lt)));
        w.Do(o, LyingClaimed, Carried, () => Assert.Equal(o, rig.Book.Take(lt)));
        w.Do(o, Carried, Loaded, () => Assert.True(raid.LoadIn(o)));
        Assert.Single(raid.Loaded);
        w.Do(o, Loaded, AtPlace, () => raid.Van.Hit(9, 1000, false));
        Assert.Empty(raid.Loaded);
        Assert.Equal(0, rig.Book.Pieces.Away);
        Assert.Equal(0, rig.Cars.Off(0x4001) & (1 << TireSource.WheelOf(o.Id)));

        // carried -> back at its place (a van that cannot take it: the Raid returns it)
        var rig2 = new Rig();
        rig2.Cars.Add(0x4001, 30, 12);
        var w2 = new Walker(rig2.Book);
        t = rig2.Book.Pick(spot, 90, spot)!.Value; o = OriginOf(t);
        rig2.Book.Take(t);
        w2.Do(o, Carried, AtPlace, () => rig2.Book.Return(o));
        Assert.Equal(0, rig2.Cars.Off(0x4001) & (1 << TireSource.WheelOf(o.Id)));
        // at its place -> lying (put down with no carrier), lying -> back at its place (sent back), claimed lying -> back at its place (sent back while a Thief goes for it)
        var spot2 = new Vector3(5, 0, 5);
        rig2.Cars.SetWheel(0x4001, 1, true);
        o = new LootOrigin(LootKind.Tire, TireSource.IdOf(0x4001, 1));
        ushort id = 0;
        w2.Do(o, AtPlace, Lying, () => id = rig2.Book.Drop(o, spot2)!.Value);
        w2.Do(o, Lying, AtPlace, () => Assert.True(rig2.Book.ReturnLying(id)));
        rig2.Cars.SetWheel(0x4001, 1, true);
        w2.Do(o, AtPlace, Lying, () => id = rig2.Book.Drop(o, spot2)!.Value);
        StealTarget claimed = default;
        w2.Do(o, Lying, LyingClaimed, () => { claimed = rig2.Book.Pick(spot2, 90, spot2)!.Value; Assert.True(claimed.Lying); });
        w2.Do(o, LyingClaimed, AtPlace, () => Assert.True(rig2.Book.ReturnLying(id)));
        rig2.Book.Release(claimed); // the Thief finds it gone and lets go: the stale claim ends, the piece stays where it is
        Assert.Equal(AtPlace, rig2.Book.Pieces.Of(o));
        Assert.Equal(0, rig2.Registry.Claimed);
        // a kind that does not lie goes back at once, one lost in a Thief's hands is gone for good; a reset clears every piece
        var rig3 = new Rig(f => { f.Loot.Tire.Weight = 0; }, tires: false);
        var pet = new FakePetSource();
        rig3.Registry.Register(pet);
        var w3 = new Walker(rig3.Book);
        t = rig3.Book.Pick(Vector3.Zero, 90, Vector3.Zero)!.Value; o = OriginOf(t);
        Assert.Equal(LootKind.Pet, o.Kind);
        w3.Do(o, Claimed, Carried, () => rig3.Book.Take(t));
        w3.Do(o, Carried, AtPlace, () => rig3.Book.Drop(o, spot));
        Assert.Equal(1, pet.Returned);
        Assert.Equal(0, rig3.Book.LyingCount);
        t = rig3.Book.Pick(Vector3.Zero, 90, Vector3.Zero)!.Value;
        rig3.Book.Take(t);
        w3.Do(o, Carried, AtPlace, () => rig3.Book.Lost(o));
        rig3.Book.Drop(new LootOrigin(LootKind.Tire, 4), spot);
        Assert.Equal(1, rig3.Book.Pieces.Away);
        rig3.Book.Reset();
        Assert.Equal(0, rig3.Book.Pieces.Away);

        var all = w.Edges.Concat(w2.Edges).Concat(w3.Edges).ToHashSet();
        var declared = LootPieces.Spec.TransitionNames.Select(e => (Enum.Parse<LootPieceState>(e.From), Enum.Parse<LootPieceState>(e.To))).ToHashSet();
        Assert.Equal(declared.OrderBy(e => e).ToList(), all.OrderBy(e => e).ToList()); // every declared edge walked, none walked that is not declared
        Assert.Empty(LootPieces.Spec.Unreachable);
    }

    /// <summary>A kind that grows back (a pole) offers a new piece under the old id while the old one is in a van: the new piece's life starts at its place.</summary>
    [Fact]
    public void AKindThatGrowsBackStartsANewLifeUnderTheOldId()
    {
        var rig = new Rig(f => f.Gang.Raid = new GangRaidCounts { GangBosses = 1, Enforcers = 0, Thieves = 0 }, tires: false);
        var pet = new FakePetSource();
        rig.Registry.Register(pet);
        var raid = rig.Launch();
        rig.RunUntil(() => raid.Van.State == RaidVanState.Opening);
        var t = rig.Book.Pick(Vector3.Zero, 90, Vector3.Zero)!.Value;
        var o = OriginOf(t);
        rig.Book.Take(t);
        Assert.True(raid.LoadIn(o));
        Assert.Equal(LootPieceState.Loaded, rig.Book.Pieces.Of(o));
        pet.Gone = false; // the source offers the same id again
        var again = rig.Book.Pick(Vector3.Zero, 90, Vector3.Zero)!.Value;
        Assert.Equal(o, OriginOf(again));
        Assert.Equal(LootPieceState.Claimed, rig.Book.Pieces.Of(o));
        rig.Book.Take(again);
        Assert.True(raid.LoadIn(o));
        Assert.Equal(LootPieceState.Loaded, rig.Book.Pieces.Of(o));
        Assert.Equal(2, raid.Loaded.Count);
    }

    [Fact]
    public void ARealThiefCarriesAPieceThroughClaimedCarriedLyingClaimedCarriedLoaded()
    {
        var rig = new Rig(f => { f.Van.StaySeconds = 1000; f.Van.Capacity = 1; });
        rig.Cars.Add(0x4001, 30, 12);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        var seen = new List<LootPieceState>();
        LootOrigin? piece = null;
        void Note() { if (piece is { } p && (seen.Count == 0 || seen[^1] != rig.Book.Pieces.Of(p))) seen.Add(rig.Book.Pieces.Of(p)); }
        rig.RunUntil(() =>
        {
            if (thief.CarriedOrigin is { } c) piece ??= c;
            if (thief.State == GangMemberState.Haul && thief.HitsTaken == 0) { Note(); thief.Hit(4, 1); } // shot once while it hauls: it drops the piece, takes it up again
            Note();
            return raid.Van.Load >= 1;
        });
        Assert.Equal(new[] { LootPieceState.Carried, LootPieceState.Lying, LootPieceState.LyingClaimed, LootPieceState.Carried, LootPieceState.Loaded }, seen);
        Assert.Equal(piece, Assert.Single(raid.Loaded));
    }

    // ---- the Raid's end ----

    [Fact]
    public void ARaidAnnouncesItsEndOnceHoweverLongItsVanLiesAsAWreck()
    {
        var rig = new Rig();
        var raid = rig.Launch();
        var ended = 0;
        raid.Ended += _ => ended++;
        rig.RunUntil(() => raid.Van.State == RaidVanState.Opening);
        rig.Step();
        Assert.Equal(0, ended);
        raid.Van.Hit(9, 1000, false);
        Assert.True(raid.Over);
        rig.Run(2);
        Assert.Equal(1, ended);
        Assert.Single(rig.Over);
        rig.Run(5);
        Assert.Equal(1, ended);
    }

    // ---- a hit makes a carrier drop its Loot ----

    static (Rig rig, Raid raid, GangMember thief) Hauling(Action<ThievesFile>? tune = null)
    {
        var rig = new Rig(f => { f.Van.StaySeconds = 1000; tune?.Invoke(f); });
        rig.Cars.Add(0x4001, 30, 12);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.Haul);
        rig.Run(0.5f);
        return (rig, raid, thief);
    }

    [Fact]
    public void AHitThatDoesNotKillMakesAHaulingThiefDropItsLootWhereItStandsAndSeekAgain()
    {
        var (rig, raid, thief) = Hauling();
        var at = thief.Position;
        var piece = thief.CarriedOrigin!.Value;
        var work = (RaidWork)raid.Gang.Work!;
        Assert.Equal(1, work.Committed);
        Assert.False(thief.Hit(4, 1f)); // a Thief takes 5
        Assert.True(thief.Alive);
        Assert.Equal(0, thief.Carried);
        Assert.Equal(GangMemberState.Seek, thief.State);
        var lying = Assert.Single(rig.Book.Lying);
        Assert.Equal(piece, lying.Origin);
        Assert.Equal(at, lying.Position);
        Assert.Equal(LootPieceState.Lying, rig.Book.Pieces.Of(piece));
        Assert.NotEqual((byte)0, rig.Cars.Off(0x4001)); // still taken until someone sends it back
        Assert.Equal(0, raid.Van.Load);
        Assert.Empty(raid.Loaded);
        Assert.Equal(0, rig.Registry.Claimed);
        Assert.Equal(0, work.Committed); // not counted against the van any more
        Assert.Equal(1f, thief.HitsTaken);
        // a player can send it back
        Assert.True(rig.Book.ReturnLying((ushort)rig.Entities.OfType(LootRules.TypeId)[0].Id));
        Assert.Equal((byte)0, rig.Cars.Off(0x4001));
        // a second hit with free hands does nothing to the Loot
        Assert.False(thief.Hit(4, 1f));
        Assert.Equal(0, rig.Book.LyingCount);
    }

    [Fact]
    public void ThePlayersHitStartsTheFightTheDroppingThiefJoinsWithFreeHands()
    {
        var (rig, raid, thief) = Hauling();
        rig.World.List.Add(new GangPlayer(4, thief.Position + new Vector3(6, 0, 0), true));
        var result = raid.Gang.Hit(thief.HitId, 4, 0, 1f, KillCause.Standard);
        Assert.Equal(DamageResult.Damaged, result);
        Assert.Equal(0, thief.Carried);
        Assert.Equal(1, rig.Book.LyingCount);
        rig.Step();
        Assert.Equal(GangMemberState.Chase, thief.State); // free hands: the aggro the hit started sends it at the player
        Assert.Equal((ushort)4, thief.Target!.Value.Id);
        Assert.Equal(1, rig.Book.LyingCount);
    }

    [Fact]
    public void WithTheSwitchOffACarrierKeepsHaulingWhenShot()
    {
        var (rig, _, thief) = Hauling(f => f.Gang.HitDropsLoot = false);
        Assert.False(thief.Hit(4, 1f));
        Assert.NotEqual(0, thief.Carried);
        Assert.Equal(GangMemberState.Haul, thief.State);
        Assert.Equal(0, rig.Book.LyingCount);
        Assert.True(ThievesFile.Parse("""{"version":1}""").Gang.HitDropsLoot); // on by default
    }

    [Fact]
    public void AKillingHitStillDropsTheLootAndACarrierThatBoardsOrIsAboutToLoadDropsToo()
    {
        var (rig, _, thief) = Hauling();
        Assert.True(thief.Hit(4, 100f));
        Assert.Equal(GangMemberState.Dead, thief.State);
        Assert.Single(rig.Book.Lying);
        // called back to board: its hands stay full (it unloads at the door), a hit does not make it seek
        var (rig2, raid2, thief2) = Hauling();
        thief2.Board(raid2.Van.DoorPoint);
        Assert.False(thief2.Hit(4, 1f));
        Assert.Equal(GangMemberState.Board, thief2.State);
        Assert.NotEqual(0, thief2.Carried);
        Assert.Equal(0, rig2.Book.LyingCount);
    }

    [Fact]
    public void ACarrierHitWhileTossingItIntoTheVanDropsItAndTheVansCountStaysTrue()
    {
        var (rig, raid, thief) = Hauling(f => f.Loot.LoadSeconds = 5f);
        rig.RunUntil(() => thief.State == GangMemberState.Load);
        Assert.Equal(0, raid.Van.Load); // counted only when it lands
        Assert.False(thief.Hit(4, 1f));
        Assert.Equal(0, raid.Van.Load);
        Assert.Empty(raid.Loaded);
        Assert.Equal(GangMemberState.Seek, thief.State);
        Assert.Single(rig.Book.Lying);
        rig.RunUntil(() => raid.Van.Load >= 1);
        Assert.Equal(1, raid.Van.Load);
        Assert.Single(raid.Loaded);
        Assert.Equal(LootPieceState.Loaded, rig.Book.Pieces.Of(raid.Loaded[0]));
    }

    [Fact]
    public void AFullVansDroppedPieceFreesItsRoomAndLoadNeverPassesCapacity()
    {
        var rig = new Rig(f => { f.Gang.Raid = new GangRaidCounts { GangBosses = 0, Enforcers = 0, Thieves = 2 }; f.Van.Capacity = 1; f.Van.StaySeconds = 1000; });
        rig.Cars.Add(0x4001, 30, 12);
        var raid = rig.Launch();
        var work = (RaidWork)raid.Gang.Work!;
        rig.RunUntil(() => raid.Gang.Members.Count == 2 && raid.Gang.Members.Any(m => m.State == GangMemberState.Haul));
        var carrier = raid.Gang.Members.First(m => m.State == GangMemberState.Haul);
        Assert.Equal(raid.Van.Capacity, work.Committed); // the van is spoken for: the other Thief finds nothing to pick
        Assert.False(carrier.Hit(4, 1f));
        Assert.Equal(0, raid.Van.Load);
        Assert.Equal(0, work.Committed);
        Assert.Equal(1, rig.Book.LyingCount);
        var most = 0;
        rig.RunUntil(() => { most = Math.Max(most, Math.Max(work.Committed, raid.Van.Load)); return raid.Van.Load >= 1; });
        rig.Run(5);
        Assert.Equal(1, most);
        Assert.Equal(1, raid.Van.Load);
        var loaded = Assert.Single(raid.Loaded);
        Assert.Equal(0, rig.Book.LyingCount);
        Assert.Equal(0, rig.Registry.Claimed);
        Assert.Equal(LootPieceState.Loaded, rig.Book.Pieces.Of(loaded));
        Assert.Equal(1, rig.Book.Pieces.Away); // only that one is away from its place
        Assert.Equal(1, Enumerable.Range(0, 4).Count(wh => (rig.Cars.Off(0x4001) & (1 << wh)) != 0)); // one wheel gone, not two
    }

    [Fact]
    public void AClimberHitWithTheThingOnItsWayDownIsPutAtTheBaseAndDropsItThere()
    {
        var rig = new Rig(f => { f.Loot.Dish.StealSeconds = 1; f.Loot.ClimbSpeed = 2f; }, tires: false);
        var src = new FakeClimbSource(new Vector3(30, 0, 12), new Vector3(30, 5, 12));
        rig.Registry.Register(src);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.ClimbDown);
        rig.Run(1);
        Assert.True(thief.Position.Y > 0.5f);
        Assert.False(thief.Hit(4, 1f));
        Assert.Equal(GangMemberState.Seek, thief.State);
        Assert.Equal(0f, thief.Position.Y);
        var lying = Assert.Single(rig.Book.Lying);
        Assert.Equal(new Vector3(30, 0, 12), lying.Position);
        Assert.True(src.Gone); // lying, still taken
    }

    [Fact]
    public void AKindThatDoesNotLieGoesBackAtOnceWhenItsCarrierIsHit()
    {
        var rig = new Rig(f => { f.Loot.Tire.Weight = 0; f.Loot.Pet.StealSeconds = 1; }, tires: false);
        var pet = new FakePetSource();
        rig.Registry.Register(pet);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.Haul);
        Assert.True(pet.Gone);
        Assert.False(thief.Hit(4, 1f));
        Assert.Equal(0, thief.Carried);
        Assert.Equal(1, pet.Returned);
        Assert.False(pet.Gone);
        Assert.Equal(0, rig.Book.LyingCount);
        Assert.Equal(0, rig.Registry.Claimed);
        Assert.Equal(0, rig.Book.Pieces.Away);
    }
}

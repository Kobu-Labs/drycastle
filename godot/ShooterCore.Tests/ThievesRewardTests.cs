using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The Thieves event's reward (docs/events/thieves/10): who is credited and paid, the Gold chain of a killed Gang boss, and the Neck slot it is worn in (the rules are the headgear's).</summary>
public class ThievesRewardTests
{
    const float Dt = 1f / 30f;
    static readonly GameData Shipped = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data"));
    static ItemsFile Items => Shipped.Items;
    static int Chain => Items.IndexOf("gold_chain");
    static int Bigfoot => Items.IndexOf("bigfoot_head");

    // ---- the data ----

    [Fact]
    public void TheShippedGoldChainIsACosmeticNeckItemWithThreePickups()
    {
        var chain = Items.Find("gold_chain")!;
        Assert.Equal(EquipSlot.Neck, chain.Slot);
        Assert.Equal(3, chain.PickupLimit);
        Assert.False(chain.HeadshotImmune); // cosmetic: it protects nothing
        Assert.NotEqual("", chain.Model);
        Assert.Equal(Items.IndexOf("bigfoot_head") + 1, Chain); // appended: the wire sends the list position
        Assert.Equal(chain.Id, Shipped.Thieves.Reward.ChainItem);
        Assert.True(Shipped.Thieves.Reward.ChainCorpseSeconds > 0);
    }

    // ---- credit ----

    [Fact]
    public void EachKindOfTakingPartCreditsThePlayerOnce()
    {
        var credit = new ThievesCredit();
        credit.NoteGangHit(3);
        credit.NoteVanHit(5);
        credit.NoteLootReturned(3);
        credit.NoteGangHit(5);
        credit.NoteVanHit(0); // no player
        Assert.Equal(new ushort[] { 3, 5 }, credit.Credited);
    }

    [Fact]
    public void AWinPaysEveryoneCreditedWhoIsStillEligibleAndForgetsThem()
    {
        var credit = new ThievesCredit();
        credit.NoteGangHit(1); credit.NoteVanHit(2); credit.NoteLootReturned(3);
        var paid = credit.Settle(won: true, id => id != 2); // 2 left the match
        Assert.Equal(new ushort[] { 1, 3 }, paid);
        Assert.Empty(credit.Credited);
        Assert.Empty(credit.Settle(won: true, _ => true)); // nothing left over for the next event
    }

    [Fact]
    public void NobodyCreditedMeansNoXpAndACancelledEventPaysNobody()
    {
        Assert.Empty(new ThievesCredit().Settle(won: true, _ => true));
        var credit = new ThievesCredit();
        credit.NoteGangHit(4);
        Assert.Empty(credit.Settle(won: false, _ => true));
        Assert.Empty(credit.Credited); // cancelled: forgotten too
    }

    [Fact]
    public void ClearForgetsEveryoneARoundResetPaysNothing()
    {
        var credit = new ThievesCredit();
        credit.NoteGangHit(4);
        credit.Clear();
        Assert.Empty(credit.Settle(won: true, _ => true));
    }

    // ---- a hit on a Gang member credits its player ----

    sealed class FakeWorld : IGangWorld
    {
        public readonly List<GangPlayer> List = new();
        public IReadOnlyList<GangPlayer> Players => List;
        public void Hurt(ushort player, float damage, ushort memberHitId) { }
    }

    sealed class Rig
    {
        public readonly FakeWorld World = new();
        public readonly WorldEntityManager Entities;
        public readonly GangRoster Roster;
        public readonly ThievesCredit Credit = new();
        public readonly IGangHits Hits;
        public readonly List<GangKilled> Kills = new();
        public int Chains;

        public Rig()
        {
            var cfg = ThievesFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "thieves.json"))).Gang;
            var types = new WorldEntityRegistry();
            GangRules.Register(types);
            Entities = new WorldEntityManager(types);
            Roster = new GangRoster(cfg, World, Entities, 7);
            Hits = new CreditedGangHits(Roster, Credit);
            Roster.Killed += Kills.Add;
            Roster.Killed += k => { if (ThievesReward.LeavesChain(k.Kind)) Chains++; }; // what the server does (NetServer.Thieves.Reward.cs)
        }

        public void Run(float seconds)
        {
            for (var t = 0f; t < seconds; t += Dt)
            {
                Roster.Update();
                Entities.Tick(Dt);
                Roster.ServeRoute((from, to) => new[] { from, to });
            }
        }

        public void KillByBullets(GangMember m, ushort by)
        {
            for (var i = 0; i < 200 && m.Alive; i++) Hits.Hit(m.HitId, by, 0, 1, KillCause.Standard);
        }
    }

    static GangMember One(ThiefGang gang, GangMemberKind kind, float x = 0) => gang.Add(kind, new Vector3(x, 0, 0), 0)!;

    [Fact]
    public void AHitThatCountedCreditsTheShooterAndOneThatDidNotDoesNot()
    {
        var rig = new Rig();
        var gang = rig.Roster.NewGang(Vector3.Zero);
        var thief = One(gang, GangMemberKind.Thief);
        Assert.Equal(DamageResult.Damaged, rig.Hits.Hit(thief.HitId, 4, 0, 1, KillCause.Standard));
        Assert.Equal(new ushort[] { 4 }, rig.Credit.Credited);

        rig.Credit.Clear();
        Assert.Equal(DamageResult.Ignored, rig.Hits.Hit(thief.HitId, 5, 0, 0, KillCause.Standard)); // no damage: ignored
        Assert.Equal(DamageResult.Ignored, rig.Hits.Hit(9999, 5, 0, 1, KillCause.Standard));        // not a member's id
        Assert.Empty(rig.Credit.Credited);
    }

    [Fact]
    public void AKillCreditsTheKillerAndALateHitOnTheBodyDoesNot()
    {
        var rig = new Rig();
        var gang = rig.Roster.NewGang(Vector3.Zero);
        var thief = One(gang, GangMemberKind.Thief);
        rig.KillByBullets(thief, 4);
        Assert.False(thief.Alive);
        rig.Credit.Clear();
        Assert.Equal(DamageResult.Ignored, rig.Hits.Hit(thief.HitId, 6, 0, 1, KillCause.Standard)); // already dead
        Assert.Empty(rig.Credit.Credited);
    }

    // ---- the chain ----

    [Fact]
    public void OnlyAKilledGangBossLeavesTheChainAndEachOneLeavesOne()
    {
        Assert.True(ThievesReward.LeavesChain(GangMemberKind.GangBoss));
        Assert.False(ThievesReward.LeavesChain(GangMemberKind.Enforcer));
        Assert.False(ThievesReward.LeavesChain(GangMemberKind.Thief));

        var rig = new Rig();
        var gang = rig.Roster.NewGang(Vector3.Zero);
        var bossA = One(gang, GangMemberKind.GangBoss);
        var bossB = One(gang, GangMemberKind.GangBoss, 2);
        var enforcer = One(gang, GangMemberKind.Enforcer, 4);
        var thief = One(gang, GangMemberKind.Thief, 6);
        rig.KillByBullets(enforcer, 4);
        rig.KillByBullets(thief, 4);
        Assert.Equal(0, rig.Chains);
        rig.KillByBullets(bossA, 4);
        Assert.Equal(1, rig.Chains);
        Assert.Equal(new Vector3(0, 0, 0), rig.Kills.Last().Where);
        rig.KillByBullets(bossB, 5);
        Assert.Equal(2, rig.Chains); // one per killed boss
        rig.Hits.Hit(bossA.HitId, 4, 0, 1, KillCause.Standard);
        Assert.Equal(2, rig.Chains); // a body is killed once
    }

    [Fact]
    public void ABossWhoFleesOffTheMapOrIsClearedLeavesNothing()
    {
        var rig = new Rig();
        var gang = rig.Roster.NewGang(Vector3.Zero);
        var fleeing = One(gang, GangMemberKind.GangBoss);
        var stays = One(gang, GangMemberKind.GangBoss, 3);
        gang.FleeAll(new Vector3(40, 0, 0));
        rig.Run(30);
        Assert.False(fleeing.Alive && fleeing.Ended == null);
        rig.Roster.Clear(); // the event ended with one still standing
        rig.Run(1);
        Assert.Equal(0, rig.Chains);
        Assert.Empty(rig.Kills);
        Assert.True(stays.Ended != null);
    }

    [Fact]
    public void ABossRunOverByAVehicleLeavesTheChainToo()
    {
        var rig = new Rig();
        var gang = rig.Roster.NewGang(Vector3.Zero);
        var boss = One(gang, GangMemberKind.GangBoss);
        Assert.Equal(DamageResult.Killed, rig.Hits.Hit(boss.HitId, 4, SpecialWeapons.Vehicle, 1, KillCause.Roadkill));
        Assert.Equal(1, rig.Chains);
        Assert.Equal(new ushort[] { 4 }, rig.Credit.Credited);
    }

    [Fact]
    public void TheChainCorpseCarriesTheChainAndIsTakenIntoTheNeckSlotThreeTimesThenCrumbles()
    {
        var types = new WorldEntityRegistry();
        CorpseRules.Register(types);
        var world = new WorldEntityManager(types);
        var book = new CorpseBook(world);
        var seconds = Shipped.Thieves.Reward.ChainCorpseSeconds;
        var id = book.Add(new Corpse(ThievesReward.ChainCorpseOwner, Vector3.Zero, 0, 0, 0, new[] { new ItemStack(Chain) }, seconds))!.Value;

        var wearer = new Equipment(Items);
        var bag = new Bag();
        Assert.Equal(ItemPickUp.Ok, book.TryPickUpItem(id, 0, Vector3.Zero, wearer, bag, Items, out var placed));
        Assert.Equal(ItemPlace.Worn, placed);
        Assert.Equal(new ItemStack(Chain, 1), wearer.Get(EquipSlot.Neck));
        Assert.Null(wearer.Get(EquipSlot.Headgear));

        // it changes hands twice more (dropped by its wearer, picked up again), and then the next attempt crumbles it
        var chain = wearer.TakeOff(EquipSlot.Neck)!.Value;
        book.Add(new Corpse(3, Vector3.Zero, 0, 0, 0, new[] { chain }));
        var second = book.Add(new Corpse(4, Vector3.Zero, 0, 0, 0, new[] { chain }))!.Value;
        Assert.Equal(ItemPickUp.Ok, book.TryPickUpItem(second, 0, Vector3.Zero, wearer, bag, Items, out _));
        Assert.Equal(2, wearer.Get(EquipSlot.Neck)!.Value.Pickups);
        var again = wearer.TakeOff(EquipSlot.Neck)!.Value;
        var third = book.Add(new Corpse(5, Vector3.Zero, 0, 0, 0, new[] { again }))!.Value;
        Assert.Equal(ItemPickUp.Ok, book.TryPickUpItem(third, 0, Vector3.Zero, wearer, bag, Items, out _));
        var last = wearer.TakeOff(EquipSlot.Neck)!.Value;
        Assert.Equal(3, last.Pickups);
        var fourth = book.Add(new Corpse(6, Vector3.Zero, 0, 0, 0, new[] { last }))!.Value;
        Assert.Equal(ItemPickUp.Crumbled, book.TryPickUpItem(fourth, 0, Vector3.Zero, wearer, bag, Items, out _));
        Assert.Null(wearer.Get(EquipSlot.Neck));
    }

    // ---- the Neck slot: as the headgear ----

    [Fact]
    public void TheNeckAndTheHeadAreTwoSlotsThatDoNotShareAnItem()
    {
        Assert.Equal(2, Equipment.SlotCount);
        Assert.Equal(Equipment.SlotCount + Bag.Cells, ItemRules.MaxCarried);
        var worn = new Equipment(Items);
        Assert.Null(worn.Wear(EquipSlot.Neck, new ItemStack(Chain)));
        Assert.Null(worn.Wear(EquipSlot.Headgear, new ItemStack(Bigfoot)));
        Assert.Equal(new ItemStack(Chain), worn.Get(EquipSlot.Neck));
        Assert.True(worn.HeadGuarded); // from the head, not the chain
        Assert.Equal(new ItemStack(Chain), worn.TakeOff(EquipSlot.Neck));
        Assert.True(worn.HeadGuarded);
        var chainOnly = new Equipment(Items);
        chainOnly.Wear(EquipSlot.Neck, new ItemStack(Chain));
        Assert.False(chainOnly.HeadGuarded); // cosmetic
    }

    [Fact]
    public void ABaggedChainIsPutOnAndTakenOffForFreeAndAFullBagRefusesTheTakeOff()
    {
        var worn = new Equipment(Items);
        var bag = new Bag();
        bag.Set(2, new ItemStack(Chain, 2));
        Assert.Equal(ItemMove.Ok, ItemRules.Equip(Items, worn, bag, 2));
        Assert.Equal(new ItemStack(Chain, 2), worn.Get(EquipSlot.Neck)); // no pickup counted
        Assert.Null(bag.Get(2));
        Assert.Equal(ItemMove.Ok, ItemRules.Unequip(worn, bag, EquipSlot.Neck));
        Assert.Equal(new ItemStack(Chain, 2), bag.Get(0));
        Assert.Null(worn.Get(EquipSlot.Neck));

        ItemRules.Equip(Items, worn, bag, 0);
        for (var i = 0; i < Bag.Cells; i++) bag.Set(i, new ItemStack(Bigfoot, i));
        Assert.Equal(ItemMove.BagFull, ItemRules.Unequip(worn, bag, EquipSlot.Neck));
        Assert.Equal(new ItemStack(Chain, 2), worn.Get(EquipSlot.Neck)); // nothing changed
    }

    [Fact]
    public void ATakenChainGoesToTheBagWhenTheNeckIsTakenAndSwapsWhenBothAreFull()
    {
        var worn = new Equipment(Items);
        var bag = new Bag();
        worn.Wear(EquipSlot.Neck, new ItemStack(Chain, 1));
        Assert.Equal(ItemPlace.Bagged, ItemRules.Place(Items, new ItemStack(Chain, 2), worn, bag, swap: true, out _));
        Assert.Equal(new ItemStack(Chain, 2), bag.Get(0));
        for (var i = 1; i < Bag.Cells; i++) bag.Set(i, new ItemStack(Bigfoot));
        Assert.Equal(ItemPlace.NoRoom, ItemRules.Place(Items, new ItemStack(Chain, 3), worn, bag, swap: false, out _)); // a ground item: nothing taken
        Assert.Equal(ItemPlace.Swapped, ItemRules.Place(Items, new ItemStack(Chain, 3), worn, bag, swap: true, out var displaced));
        Assert.Equal(new ItemStack(Chain, 1), displaced);
        Assert.Equal(new ItemStack(Chain, 3), worn.Get(EquipSlot.Neck));
    }

    [Fact]
    public void ADeathPutsTheChainAndTheHeadOnTheCorpseWornFirst()
    {
        var worn = new Equipment(Items);
        var bag = new Bag();
        worn.Wear(EquipSlot.Neck, new ItemStack(Chain, 1));
        worn.Wear(EquipSlot.Headgear, new ItemStack(Bigfoot, 2));
        bag.Set(0, new ItemStack(Chain, 3));
        var all = ItemRules.Strip(worn, bag);
        Assert.Equal(new[] { new ItemStack(Bigfoot, 2), new ItemStack(Chain, 1), new ItemStack(Chain, 3) }, all); // slot order, then the bag
        Assert.All(worn.Slots, s => Assert.Null(s));
        Assert.True(bag.Count == 0);
    }

    [Fact]
    public void TheEquipmentEventCarriesBothSlotsAndTheWornEventToo()
    {
        var worn = new Equipment(Items);
        worn.Wear(EquipSlot.Neck, new ItemStack(Chain, 2));
        var bag = new Bag();
        Assert.True(ItemEvents.TryDecodeEquipment(ItemEvents.EncodeEquipment(worn, bag), out var slots, out var cells));
        Assert.Equal(new ItemStack?[] { null, new ItemStack(Chain, 2) }, slots);
        Assert.Equal(Bag.Cells, cells.Length);
        Assert.True(ItemEvents.TryDecodeWorn(ItemEvents.EncodeWorn(9, worn), out var player, out var others));
        Assert.Equal((ushort)9, player);
        Assert.Equal(new int?[] { null, Chain }, others); // no pickup count for the others
        Assert.True(Wire.ProtocolVersion >= 44);
    }

    [Fact]
    public void TheEquipmentScreenWalksFromTheHeadgearSlotDownToTheNeckSlot()
    {
        var head = CellRef.Slot(EquipSlot.Headgear);
        var neck = CellRef.Slot(EquipSlot.Neck);
        Assert.Equal(neck, EquipmentNav.Move(head, NavDir.Down));
        Assert.Equal(head, EquipmentNav.Move(neck, NavDir.Up));
        Assert.Null(EquipmentNav.Move(neck, NavDir.Down));
        // a chain in the bag is put on in the neck slot: the click on it equips, and a drag onto the neck cell is accepted, onto the head cell is not
        Func<CellRef, EquipSlot?> kit = c => c.Home == CellHome.Bag && c.Index == 0 ? EquipSlot.Neck : null;
        Assert.Equal(WishKind.Equip, EquipmentWishes.Activate(CellRef.BagCell(0), kit, bagFull: false).Kind);
        Assert.True(EquipmentWishes.CanDrop(CellRef.BagCell(0), DropTarget.Onto(neck), kit));
        Assert.False(EquipmentWishes.CanDrop(CellRef.BagCell(0), DropTarget.Onto(head), kit));
    }
}

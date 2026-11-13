using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Issue #261: items.json, the headgear slot and the Bigfoot head (a headshot on its wearer is a body hit). The corpse pickup limit is in <see cref="CorpseItemTests"/>.</summary>
public class ItemTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);
    static ItemsFile Items => Shipped.Items;
    static int Bigfoot => Items.IndexOf("bigfoot_head");

    // ---- the data ----

    [Fact]
    public void TheShippedBigfootHeadIsHeadgearThatStopsHeadshotsAndCrumblesAfterThreePickups()
    {
        var head = Items.Find("bigfoot_head")!;
        Assert.Equal(EquipSlot.Headgear, head.Slot);
        Assert.True(head.HeadshotImmune);
        Assert.Equal(3, head.PickupLimit);
        Assert.Equal(head, Items.At(Bigfoot));
    }

    [Theory]
    [InlineData("""{"version":1,"items":[{"id":"a","name":"A"},{"id":"a","name":"B"}]}""")]
    [InlineData("""{"version":1,"items":[{"id":"Big Foot","name":"A"}]}""")]
    [InlineData("""{"version":1,"items":[{"id":"a","name":""}]}""")]
    [InlineData("""{"version":1,"items":[{"id":"a","name":"A","pickup_limit":0}]}""")]
    [InlineData("""{"version":1,"items":[{"id":"a","name":"A","slot":"feet"}]}""")]
    public void ABadItemTableIsRefused(string json) => Assert.Throws<DataFileException>(() => ItemsFile.Parse(json));

    [Fact]
    public void AnItemWithNoPickupLimitCanBeTakenAnyNumberOfTimes()
    {
        var items = ItemsFile.Parse("""{"version":1,"items":[{"id":"hat","name":"Hat"}]}""");
        Assert.Null(items.Items[0].PickupLimit);
        Assert.Equal(ItemPickUp.Ok, ItemRules.Take(items, new ItemStack(0, 1000), out var taken));
        Assert.Equal(1001, taken.Pickups);
    }

    // ---- what a player wears ----

    [Fact]
    public void WearingTheBigfootHeadGuardsTheHeadUntilItIsTakenOff()
    {
        var worn = new Equipment(Items);
        Assert.False(worn.HeadGuarded);
        Assert.Null(worn.Wear(EquipSlot.Headgear, new ItemStack(Bigfoot)));
        Assert.True(worn.HeadGuarded);
        Assert.True(worn.Changed);
        Assert.Equal(new ItemStack(Bigfoot), worn.TakeOff(EquipSlot.Headgear));
        Assert.False(worn.HeadGuarded);
        Assert.Null(worn.TakeOff(EquipSlot.Headgear));
    }

    // ---- a headshot on the wearer ----

    [Fact]
    public void AHeadshotOnTheWearerIsABodyHitWithBodyDamage()
    {
        var ak = Shipped.Weapons.Weapons.Single(w => w.Id == "ak47");
        var bare = new HitTarget(2, new Vector3(0, 0, -10), Stance.Stand, 0);
        var wearing = bare with { HeadGuarded = true };
        var eye = new Vector3(0, 1.7f, 0);
        var aim = Vector3.Normalize(HitMath.HeadCenter(bare) - eye);

        var headshot = HitMath.Raycast(eye, aim, 100, float.PositiveInfinity, new[] { bare }, 1)!.Value;
        var guarded = HitMath.Raycast(eye, aim, 100, float.PositiveInfinity, new[] { wearing }, 1)!.Value;

        Assert.True(headshot.Head);
        Assert.False(guarded.Head);                                      // still a hit on the same ray, but not a headshot
        Assert.Equal(headshot.Distance, guarded.Distance, 3);
        Assert.Equal(30f, HitMath.HitscanDamage(ak, guarded.Distance, guarded.Head)); // the AK's body damage: no multiplier, no marker
        Assert.True(HitMath.HitscanDamage(ak, headshot.Distance, true) > HitMath.HitscanDamage(ak, guarded.Distance, guarded.Head));
    }

    [Fact]
    public void AShotgunFanOnTheWearerNeverCountsAHeadshot()
    {
        var shotgun = Shipped.Weapons.Weapons.First(w => w.Pellets > 1);
        var wearing = new HitTarget(2, new Vector3(0, 0, -6), Stance.Stand, 0, HeadGuarded: true);
        var eye = new Vector3(0, 1.7f, 0);
        var aim = Vector3.Normalize(HitMath.HeadCenter(wearing) - eye);
        var dirs = Enumerable.Repeat(aim, 4).ToArray(); // four pellets straight at the head
        var walls = Enumerable.Repeat(float.PositiveInfinity, 4).ToArray();
        var totals = HitMath.ResolvePellets(shotgun, eye, dirs, 100, walls, new[] { wearing }, 1);
        Assert.False(totals[2].Head);
        Assert.True(totals[2].Damage > 0);
    }
}

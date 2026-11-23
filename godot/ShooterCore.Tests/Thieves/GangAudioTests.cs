using System.Numerics;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Thieves;

/// <summary>The Thieves' audio (docs/events/thieves/09): the voice roll rules (range, cooldown, one line at a time per Raid), the seeded speaker roll, the wire (event 236, hp byte bit 6) and the bag-only speaker Item.</summary>
public class GangAudioTests
{
    const float Dt = 1f / 30f;

    sealed class FakeWorld : IGangWorld
    {
        public readonly List<GangPlayer> List = new();
        public IReadOnlyList<GangPlayer> Players => List;
        public void Hurt(ushort player, float damage, ushort memberHitId) { }
    }

    sealed class Rig
    {
        public readonly GangConfig Gang = ThievesFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "thieves.json"))).Gang;
        public readonly ThievesAudioConfig Audio = new() { VoiceChance = 1, VoiceRollS = 0.5f, VoiceCooldownS = 20, VoiceBusyS = 3, SpeakerChance = 0 };
        public readonly FakeWorld World = new();
        public readonly WorldEntityManager Entities;
        public readonly GangRoster Roster;
        public readonly GangAudio Audio_;
        public readonly List<(GangMember Member, byte Line, float At)> Said = new();
        public float Clock;

        public Rig(int seed = 1)
        {
            var types = new WorldEntityRegistry();
            GangRules.Register(types);
            Entities = new WorldEntityManager(types);
            Roster = new GangRoster(Gang, World, Entities, 3);
            Audio_ = new GangAudio(Audio, seed);
        }

        public GangMember Member(ThiefGang gang, float x, float z, GangMemberKind kind = GangMemberKind.Thief) => gang.Add(kind, new Vector3(x, 0, z), 0)!;
        public void Player(float x, float z) => World.List.Add(new GangPlayer(1, new Vector3(x, 0, z), true));

        public void Run(float seconds)
        {
            for (var t = 0f; t < seconds; t += Dt)
            {
                Roster.Update();
                Entities.Tick(Dt);
                Audio_.Update(Dt, Roster.Gangs, World.List, (m, l) => Said.Add((m, l, Clock)));
                Clock += Dt;
            }
        }
    }

    // ---- data and wire ----

    [Fact]
    public void ShippedNumbersAreTheTicketsAndTheFileRejectsNonsense()
    {
        var a = ThievesFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "thieves.json"))).Audio;
        Assert.Equal((8f, 3f, 25f, 15), (a.VoiceNearM, a.VoiceBusyS, a.VoiceRangeM, a.VoiceLines));
        Assert.Equal((0.05f, 30f, 120f, 8f), (a.SpeakerChance, a.SpeakerRangeM, a.VanRangeM, a.VanVolumeDb));
        Assert.Equal(new ThievesAudioConfig().VoiceChance, ThievesFile.Parse("""{"version":1}""").Audio.VoiceChance); // a file without the section has the defaults
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"audio":{"voice_chance":1.5}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"audio":{"voice_near_m":0}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"audio":{"voice_lines":0}}"""));
    }

    [Fact]
    public void TheVoiceEventRoundTripsAndRefusesTheWrongLength()
    {
        Assert.Equal(236, (int)EventKind.ThiefVoice);
        var bytes = GangVoiceCodec.Encode(0xBEEF, 14);
        Assert.Equal(GangVoiceCodec.Bytes, bytes.Length);
        Assert.True(GangVoiceCodec.TryDecode(bytes, out var id, out var line));
        Assert.Equal((0xBEEF, 14), (id, line));
        Assert.False(GangVoiceCodec.TryDecode(new byte[GangVoiceCodec.Bytes - 1], out _, out _));
        Assert.False(GangVoiceCodec.TryDecode(new byte[GangVoiceCodec.Bytes + 1], out _, out _));
    }

    [Fact]
    public void TheSpeakerIsBit6OfTheHpByteAndOnlyWhileAlive()
    {
        var hp = GangRules.PackHp(GangMemberKind.Enforcer, true, 5, speaker: true);
        Assert.True(GangRules.SpeakerOf(hp));
        Assert.Equal((GangMemberKind.Enforcer, true, (byte)5), (GangRules.KindOf(hp), GangRules.AliveOf(hp), GangRules.CarriedOf(hp)));
        Assert.Equal(64, hp & 64);
        Assert.False(GangRules.SpeakerOf(GangRules.PackHp(GangMemberKind.Enforcer, true, 5)));

        var rig = new Rig();
        var m = rig.Member(rig.Roster.NewGang(Vector3.Zero), 0, 0);
        Assert.False(GangRules.SpeakerOf(m.Serialize().Hp));
        m.Speaker = true;
        Assert.True(GangRules.SpeakerOf(m.Serialize().Hp));
        m.Kill(0);
        Assert.False(GangRules.SpeakerOf(m.Serialize().Hp)); // it stops with the death
    }

    // ---- the voice rules ----

    [Fact]
    public void ALineNeedsAPlayerWithinTheNearRange()
    {
        var rig = new Rig();
        var m = rig.Member(rig.Roster.NewGang(Vector3.Zero), 0, 0);
        rig.Run(5);
        Assert.Empty(rig.Said); // nobody near

        rig.Player(8.5f, 0);
        rig.Run(5);
        Assert.Empty(rig.Said); // just out of 8 m

        rig.World.List.Clear(); rig.Player(7.5f, 0);
        rig.Run(2);
        Assert.Equal(m, Assert.Single(rig.Said).Member);
        Assert.InRange(rig.Said[0].Line, 0, rig.Audio.VoiceLines - 1);
    }

    [Fact]
    public void AMemberWaitsOutItsCooldownAndTheRollClockOnlyRunsWhileSomeoneIsNear()
    {
        var rig = new Rig();
        rig.Member(rig.Roster.NewGang(Vector3.Zero), 0, 0);
        rig.Player(2, 0);
        rig.Run(19);
        Assert.Single(rig.Said); // chance 1, yet once in 19 s: the cooldown is 20
        rig.Run(3);
        Assert.Equal(2, rig.Said.Count);
        Assert.InRange(rig.Said[1].At - rig.Said[0].At, 19.9f, 22f);
    }

    [Fact]
    public void ARaidLetsOneLineThroughAtATime()
    {
        var rig = new Rig();
        var gang = rig.Roster.NewGang(Vector3.Zero);
        for (var i = 0; i < 4; i++) rig.Member(gang, i, 0);
        rig.Player(2, 1);
        rig.Run(2.9f);
        Assert.Single(rig.Said); // three more could, but the Raid is busy for 3 s
        rig.Run(1.5f);
        Assert.Equal(2, rig.Said.Count);
        Assert.True(rig.Said[1].At - rig.Said[0].At >= rig.Audio.VoiceBusyS - 0.1f);
        Assert.NotEqual(rig.Said[0].Member, rig.Said[1].Member); // the first is in its cooldown
        Assert.NotEqual(rig.Said[0].Line, rig.Said[1].Line);     // and a Raid does not repeat the line it just said
    }

    [Fact]
    public void TwoRaidsSpeakAtOnce()
    {
        var rig = new Rig();
        rig.Member(rig.Roster.NewGang(Vector3.Zero), 0, 0);
        rig.Member(rig.Roster.NewGang(new Vector3(4, 0, 0)), 4, 0);
        rig.Player(2, 0);
        rig.Run(1.2f);
        Assert.Equal(2, rig.Said.Count);
    }

    [Fact]
    public void NoLineIsSaidInAWindupOrSwingOrByTheDead()
    {
        var rig = new Rig();
        var gang = rig.Roster.NewGang(Vector3.Zero);
        var boss = rig.Member(gang, 0, 0, GangMemberKind.GangBoss);
        rig.Player(1, 0);
        gang.NoteHit(1);
        rig.Audio.VoiceCooldownS = 0; rig.Audio.VoiceBusyS = 0; rig.Audio.VoiceRollS = Dt;
        var sawBlow = false;
        for (var t = 0f; t < 8f; t += Dt)
        {
            var n = rig.Said.Count;
            rig.Run(Dt);
            var blow = boss.State is GangMemberState.Windup or GangMemberState.Swing;
            sawBlow |= blow;
            if (blow) Assert.Equal(n, rig.Said.Count);
        }
        Assert.True(sawBlow);
        Assert.NotEmpty(rig.Said);

        boss.Kill(1); // and the dead are silent
        Assert.False(GangAudio.CanSpeak(boss));
        var before = rig.Said.Count;
        rig.Run(5);
        Assert.Equal(before, rig.Said.Count);
    }

    [Fact]
    public void GoneMembersAndRaidsAreForgotten()
    {
        var rig = new Rig();
        var gang = rig.Roster.NewGang(Vector3.Zero);
        rig.Member(gang, 0, 0); rig.Member(gang, 1, 0);
        rig.Run(0.1f);
        Assert.Equal(2, rig.Audio_.Tracked);
        rig.Roster.Remove(gang);
        rig.Run(0.1f);
        Assert.Equal(0, rig.Audio_.Tracked);
    }

    // ---- the speaker roll ----

    [Fact]
    public void TheSpeakerRollIsSeededAndFollowsTheChance()
    {
        static List<bool> Rolls(int seed, float chance, int n) { var r = new Random(seed); return Enumerable.Range(0, n).Select(_ => GangAudio.RollSpeaker(r, chance)).ToList(); }
        Assert.Equal(Rolls(42, 0.05f, 500), Rolls(42, 0.05f, 500));
        Assert.NotEqual(Rolls(42, 0.5f, 500), Rolls(43, 0.5f, 500));
        Assert.DoesNotContain(true, Rolls(1, 0, 200));
        Assert.DoesNotContain(false, Rolls(1, 1, 200));
        Assert.InRange(Rolls(7, 0.05f, 10000).Count(x => x), 380, 620);
    }

    [Fact]
    public void AMemberRollsItsSpeakerOnceWhenFirstSeen()
    {
        var rig = new Rig(seed: 5);
        rig.Audio.SpeakerChance = 1;
        var gang = rig.Roster.NewGang(Vector3.Zero);
        var a = rig.Member(gang, 0, 0);
        Assert.False(a.Speaker);
        rig.Run(0.1f);
        Assert.True(a.Speaker);
        rig.Audio.SpeakerChance = 0;
        var b = rig.Member(gang, 1, 0);
        rig.Run(0.1f);
        Assert.False(b.Speaker);
        Assert.True(a.Speaker); // not rolled again
    }

    [Fact]
    public void TheSameSeedMakesTheSameSpeakers()
    {
        List<bool> Crew(int seed)
        {
            var rig = new Rig(seed);
            rig.Audio.SpeakerChance = 0.3f;
            var gang = rig.Roster.NewGang(Vector3.Zero);
            var ms = Enumerable.Range(0, 20).Select(i => rig.Member(gang, i, 0)).ToList();
            rig.Run(0.1f);
            return ms.Select(m => m.Speaker).ToList();
        }
        Assert.Equal(Crew(9), Crew(9));
        Assert.Contains(true, Crew(9));
        Assert.Contains(false, Crew(9));
    }

    // ---- the speaker Item ----

    static readonly ItemsFile Items = ItemsFile.Parse("""{"version":1,"items":[{"id":"hat","name":"Hat"},{"id":"speaker","name":"Speaker","bag_only":true}]}""");
    const int Speaker = 1;

    [Fact]
    public void TheShippedSpeakerIsAPlainBagItemAfterTheBigfootHead()
    {
        var items = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Items;
        Assert.True(items.IndexOf("speaker") > items.IndexOf("bigfoot_head"));
        Assert.True(items.Find("speaker")!.BagOnly);
        Assert.False(items.Find("bigfoot_head")!.BagOnly);
    }

    [Fact]
    public void ABagOnlyItemGoesToTheBagNeverToTheSlotAndCannotBePutOn()
    {
        var worn = new Equipment(Items);
        var bag = new Bag();
        Assert.Equal(ItemPlace.Bagged, ItemRules.Place(Items, new ItemStack(Speaker), worn, bag, swap: false, out _));
        Assert.Null(worn.Get(EquipSlot.Headgear));
        Assert.Equal(ItemMove.Nothing, ItemRules.Equip(Items, worn, bag, 0));
        Assert.Null(worn.Get(EquipSlot.Headgear));
        Assert.Equal(new ItemStack(Speaker), bag.Get(0));

        for (var i = 1; i < Bag.Cells; i++) bag.Add(new ItemStack(0));
        Assert.Equal(ItemPlace.NoRoom, ItemRules.Place(Items, new ItemStack(Speaker), worn, bag, swap: true, out _)); // no slot to swap into
        Assert.Null(worn.Get(EquipSlot.Headgear));
    }

    [Fact]
    public void ABagOnlyItemIsNotLostWhenTheBagIsFullAndNoPickupIsCounted()
    {
        var types = new WorldEntityRegistry();
        CorpseRules.Register(types); GroundItemRules.Register(types);
        var book = new CorpseBook(new WorldEntityManager(types));
        var worn = new Equipment(Items);
        var bag = new Bag();
        for (var i = 0; i < Bag.Cells; i++) bag.Add(new ItemStack(0));

        var id = book.Drop(0, Vector3.Zero, new ItemStack(Speaker))!.Value;
        Assert.Equal(ItemPickUp.NoRoom, book.TryTakeGround(id, Vector3.Zero, worn, bag, Items, out _));
        Assert.NotNull(book.GetGround(id)); // it lies there still, and the empty slot did not take it

        bag.Remove(2);
        Assert.Equal(ItemPickUp.Ok, book.TryTakeGround(id, Vector3.Zero, worn, bag, Items, out var placed));
        Assert.Equal(ItemPlace.Bagged, placed);
        Assert.Equal(new ItemStack(Speaker, 1), bag.Get(2));
    }

    [Fact]
    public void TheEquipmentScreenOffersNoPutOnForABagOnlyItem()
    {
        var hat = EquipSlot.Headgear;
        Func<CellRef, EquipSlot?> slotOf = c => c.Home == CellHome.Bag && c.Index == 0 ? hat : null;
        Func<CellRef, bool> plain = c => c.Home == CellHome.Bag && c.Index == 0;
        var cell = CellRef.BagCell(0);
        Assert.Equal(WishKind.Equip, EquipmentWishes.Activate(cell, slotOf, false).Kind);
        Assert.Equal(WishKind.None, EquipmentWishes.Activate(cell, slotOf, false, plain).Kind);
        Assert.Equal(WishKind.None, EquipmentWishes.Drag(cell, DropTarget.Avatar, slotOf, plain).Kind);
        Assert.Equal(WishKind.None, EquipmentWishes.Drag(cell, DropTarget.Onto(CellRef.Slot(hat)), slotOf, plain).Kind);
        Assert.Equal(WishKind.DropBagged, EquipmentWishes.Drag(cell, DropTarget.Ground, slotOf, plain).Kind); // it can still be dropped
        Assert.Equal(WishKind.DropBagged, EquipmentWishes.Drop(cell, slotOf).Kind);
    }
}

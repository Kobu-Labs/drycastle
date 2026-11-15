using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The Thieves' look (docs/events/thieves/08): what a member wears and plays comes from its look number and thieves.json alone, so every client agrees; the blow clips land with the server's blow; the van's box is the model's; a member in the electrocution states does not speak.</summary>
public class GangViewTests
{
    static readonly ThievesFile Shipped = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Thieves;
    static GangView View => Shipped.Gang.View;

    [Fact]
    public void EveryShippedClipIsNamedAndNothingIsAPeasantClip()
    {
        var c = View.Clips;
        var names = new[] { c.Idle, c.Walk, c.Run, c.Sprint, c.Steal, c.StealLoop, c.ClimbUp, c.ClimbDown, c.ClimbWork, c.KnockdownBegin, c.KnockdownLoop, c.KnockdownEnd, c.StunLoop, c.HopIn, c.HopOut, c.Land }
            .Concat(c.Death).Concat(View.Blows.Thief.Select(b => b.Clip)).Concat(View.Blows.Enforcer.Select(b => b.Clip)).Concat(View.Blows.GangBoss.Select(b => b.Clip))
            .Concat(View.Gestures.Taunts).Append(View.Gestures.Drink).ToList();
        Assert.All(names, n => Assert.NotEqual("", n));
        Assert.DoesNotContain("sprint_up25f_femn", names);
        Assert.Equal(3, c.Death.Count);
        Assert.DoesNotContain("kneel_exit", string.Join(' ', names)); // the pack's 7.5 s way up is never played
        // the pack splits these by body; the data names the femn model
        Assert.Equal(new[] { "run_f_masc", "run_f_femn" }, new[] { false, true }.Select(f => GangLook.ClipName(c.Run, f)));
        Assert.Contains("gang_thief_02", View.Models.Femn);
        Assert.Equal("idle_standing_femn_g", GangLook.ClipName(c.Idle, true));
        Assert.Equal("climb_up", GangLook.ClipName(c.ClimbUp, true));
    }

    [Fact]
    public void TheKindsWearTheirModelsAndAThiefIsOneOfTwo()
    {
        Assert.Equal(new[] { "gang_boss_01" }, View.Models.GangBoss);
        Assert.Equal(new[] { "gang_enforcer_01" }, View.Models.Enforcer);
        Assert.Equal(new[] { "gang_thief_01", "gang_thief_02" }, View.Models.Thief.Order());
        var thieves = Enumerable.Range(0, 2000).Select(l => GangLook.Model(View, GangMemberKind.Thief, (ushort)l)).ToList();
        Assert.Equal(2, thieves.Distinct().Count());
        Assert.InRange(thieves.Count(t => t == "gang_thief_02"), 800, 1200);
        Assert.All(Enumerable.Range(0, 50), l => Assert.Equal("gang_boss_01", GangLook.Model(View, GangMemberKind.GangBoss, (ushort)l)));
        // the same look is the same member on every client
        Assert.Equal(GangLook.Atlas(View, 777), GangLook.Atlas(View, 777));
        Assert.Equal(GangLook.Model(View, GangMemberKind.Thief, 4321), GangLook.Model(View, GangMemberKind.Thief, 4321));
    }

    [Fact]
    public void TheAtlasIsAPlainUniformPickOverAllTwelve()
    {
        Assert.Equal(12, View.Atlases.Count);
        Assert.Equal(12, View.Atlases.Distinct().Count());
        Assert.All(View.Atlases, a => Assert.StartsWith("gang-warfare/textures/PolygonGangWarfare_0", a));
        var counts = new int[12];
        const int n = 24000;
        for (var l = 0; l < n; l++) counts[View.Atlases.IndexOf(GangLook.Atlas(View, (ushort)(l * 2 + 1 & 0xFFFF)))]++;
        Assert.All(counts, c => Assert.InRange(c, n / 12 * 0.85, n / 12 * 1.15));
    }

    [Fact]
    public void AboutOneInThreeSmokesAndTheChoicesDoNotLockStep()
    {
        var looks = Enumerable.Range(0, 30000).Select(l => (ushort)(l * 2 + 1 & 0xFFFF)).ToList();
        var smoking = looks.Count(l => GangLook.Smokes(View, l));
        Assert.InRange(smoking / (double)looks.Count, 0.30, 0.37);
        // smoking says nothing about the atlas or the body
        var smokersOnThief2 = looks.Where(l => GangLook.Smokes(View, l)).Count(l => GangLook.Model(View, GangMemberKind.Thief, l) == "gang_thief_02");
        Assert.InRange(smokersOnThief2 / (double)smoking, 0.45, 0.55);
        Assert.InRange(looks.Where(l => GangLook.Smokes(View, l)).Select(l => GangLook.Atlas(View, l)).Distinct().Count(), 12, 12);
        Assert.All(looks.Take(500), l => Assert.Contains(GangLook.Death(View, l), View.Clips.Death));
    }

    [Fact]
    public void ABlowLandsWithTheServersBlowForEveryShippedClip()
    {
        foreach (var kind in new[] { GangMemberKind.Thief, GangMemberKind.Enforcer, GangMemberKind.GangBoss })
        {
            var windup = Shipped.Gang.Of(kind).Melee.WindupSeconds;
            Assert.NotEmpty(View.Blows.Of(kind));
            foreach (var blow in View.Blows.Of(kind))
            {
                var plan = GangLook.Plan(blow, windup);
                Assert.True(plan.FromS >= blow.SeekS - 1e-4f, $"{blow.Clip} starts before its motion");
                Assert.True(plan.DelayS >= 0, blow.Clip);
                Assert.Equal(windup, plan.DelayS + (blow.PeakS - plan.FromS), 3); // wait, then play to the peak: the peak is windup seconds after the windup began
            }
        }
        // the notes' examples: swing_a is short (waits 0.12 s), swing_c has more clip than windup (starts into it, no wait)
        var a = GangLook.Plan(new GangBlow { Clip = "swing_a", SeekS = 0.07f, PeakS = 0.40f }, 0.45f);
        Assert.Equal(0.07f, a.FromS, 3); Assert.Equal(0.12f, a.DelayS, 3);
        var c = GangLook.Plan(new GangBlow { Clip = "swing_c", SeekS = 1.45f, PeakS = 1.93f }, 0.45f);
        Assert.Equal(1.48f, c.FromS, 3); Assert.Equal(0f, c.DelayS);
        // a kind with no blow list leans in code
        Assert.Null(GangLook.Blow(new GangView(), GangMemberKind.Thief, 5, 0));
        // the swings of one member vary
        Assert.True(Enumerable.Range(0, 6).Select(i => GangLook.Blow(View, GangMemberKind.Enforcer, 9, i)!.Clip).Distinct().Count() == 3);
    }

    [Fact]
    public void TheClimbClipPlaysAtTheClimbSpeed()
    {
        Assert.Equal(1.5f / 0.489f, GangLook.ClimbPlayback(View.Climb, Shipped.Loot.ClimbSpeed), 3);
        Assert.Equal(1f, GangLook.ClimbPlayback(View.Climb, 0.489f), 3);
        // the member stands as far from the surface as the targets put it
        Assert.Equal(Shipped.Loot.Dish.BaseOutM, View.Climb.SurfaceM);
        Assert.Equal(Shipped.Loot.Connector.BaseOffsetM, View.Climb.SurfaceM);
    }

    [Fact]
    public void TheCarryPosesAndDressAreCompleteAndTheViewValidates()
    {
        foreach (var kind in LootKinds.All)
        {
            Assert.True(View.Carry.Kinds.TryGetValue(kind.ToString().ToLowerInvariant(), out var k), kind.ToString());
            Assert.True(View.Carry.Poses.ContainsKey(k!.Pose));
        }
        Assert.Equal("two_hands", View.Carry.Kinds["tire"].Pose);
        Assert.Equal("two_hands", View.Carry.Kinds["dish"].Pose);
        Assert.Equal("under_arm", View.Carry.Kinds["pet"].Pose);
        Assert.Equal("one_hand", View.Carry.Kinds["connector"].Pose);
        Assert.Equal("props/tire_01", View.Carry.Kinds["tire"].Model);
        Assert.NotNull(View.Carry.Poses["under_arm"].PetOffset); // the pet pose follows loot.pet carry_*
        Assert.Equal(3, View.Dress.Cigarette.OneIn);
        Assert.Equal("Hand_R", View.Dress.Pipe.Bone);
        Assert.Equal(new[] { -0.0879f, 0.0391f, 0.0163f }, View.Dress.Pipe.Position);
        Assert.Equal(4, View.Dress.Pipe.Rotation.Length);
        // bad data is refused with a message, not a crash at the first frame
        string Parse(string gang) => Assert.Throws<DataFileException>(() => ThievesFile.Parse("{\"version\":1,\"gang\":{\"view\":" + gang + "}}")).Message;
        Assert.Contains("carry.kinds.tire.pose", Parse("""{"carry":{"kinds":{"tire":{"pose":"nope"}}}}"""));
        Assert.Contains("atlases", Parse("""{"atlases":[]}"""));
        Assert.Contains("blows", Parse("""{"blows":{"thief":[{"clip":"x","seek_s":2,"peak_s":1}]}}"""));
        Assert.Contains("one_in", Parse("""{"dress":{"cigarette":{"one_in":0}}}"""));
        Assert.Contains("models", Parse("""{"models":{"thief":[]}}"""));
    }

    // ---- the van ----

    [Fact]
    public void TheVansBoxIsTheModelsAndItsDoorIsWhereTheModelsDoorIs()
    {
        var v = Shipped.Van;
        // the model is 2.417 x 1.860 x 4.753 m (raid_van.json of the pack): the numbers are the model's
        Assert.Equal(2.417f / 2, v.HalfWidthM, 0.01f);
        Assert.Equal(4.753f / 2, v.HalfLengthM, 0.01f);
        Assert.Equal(1.860f, v.HeightM, 0.01f);
        // the crew steps out in front of the doorway: its middle is 0.068 ahead of the centre (the door's z, +Z is forward)
        Assert.Equal(0.068f, v.DoorAlongM, 0.05f);
        // the hit capsules (prone body 1.8 m grown by hit_scale, offset ahead and behind) end where the model does
        Assert.Equal(v.HalfLengthM, v.HitOffsetM + 1.8f / 2 * v.HitScale, 0.05f);
        Assert.Equal("vehicles/raid_van", v.View.Model);
        Assert.Equal("door", v.View.DoorNode);
        Assert.Equal(new[] { -0.722f, 0.763f, 0.068f }, v.View.DoorClosed);
        Assert.Equal(new[] { -0.842f, 0.763f, -0.932f }, v.View.DoorOpen);
        // (the door point's side, either side, is RaidVanTests: ADoorPointIsBesideTheSideOfTheVanOnTheSideOfItsDoor; the client mirrors the model for a left door)
    }

    // ---- the voice (ticket 07's open point) ----

    [Fact]
    public void AMemberWhoIsShockedFallingDownOrGettingUpDoesNotSpeak()
    {
        foreach (var s in Enum.GetValues<GangMemberState>())
        {
            var silent = s is GangMemberState.Windup or GangMemberState.Swing or GangMemberState.Shocked or GangMemberState.Falling or GangMemberState.Down or GangMemberState.GetUp;
            Assert.Equal(!silent, GangAudio.CanSpeakIn(s));
        }
        Assert.True(GangAudio.CanSpeakIn(GangMemberState.Idle));
        Assert.True(GangAudio.CanSpeakIn(GangMemberState.ClimbUp));
    }
}

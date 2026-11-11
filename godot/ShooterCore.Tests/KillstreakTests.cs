using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Ticket #118: killstreaks.json, the streak counter, earning, activation, duration, control and the events.</summary>
public class KillstreakTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);
    static KillstreakFile Shipping => Shipped.Killstreaks;

    const ushort Player = 7, Enemy = 8;
    static readonly Vector3 Here = new(1, 0, 2);

    sealed class Harness
    {
        public readonly List<(ushort Owner, EventKind Kind, byte[] Payload)> Sent = new();
        public readonly List<string> Log = new();
        public readonly KillstreakService Service;

        public Harness(KillstreakFile data, IEnumerable<IKillstreak>? impl = null) =>
            Service = new KillstreakService(data, impl ?? data.Streaks.Select(s => (IKillstreak)new LoggingKillstreak(s.Id)),
                (o, k, p) => Sent.Add((o, k, p)), Log.Add);

        public IEnumerable<EventKind> KindsTo(ushort owner) => Sent.Where(s => s.Owner == owner).Select(s => s.Kind);
    }

    /// <summary>Kill the enemy as <paramref name="killer"/>, n times.</summary>
    static void Kills(Harness h, ushort killer, int n, bool fromStreak = false)
    {
        for (var i = 0; i < n; i++) h.Service.OnKill(killer, Enemy, fromStreak);
    }

    /// <summary>The shipped table with one streak's implementation replaced.</summary>
    static IEnumerable<IKillstreak> With(string id, IKillstreak replacement) =>
        Shipping.Streaks.Select(s => s.Id == id ? replacement : (IKillstreak)new LoggingKillstreak(s.Id));

    /// <summary>A test streak whose activation is a delegate.</summary>
    sealed class ScriptedStreak : IKillstreak
    {
        readonly Func<KillstreakUse, bool> _activate;
        public ScriptedStreak(string id, Func<KillstreakUse, bool> activate, IReadOnlySet<string>? owned = null)
        {
            Id = id; _activate = activate; OwnedWeapons = owned ?? new HashSet<string>();
        }
        public string Id { get; }
        public IReadOnlySet<string> OwnedWeapons { get; }
        public bool Activate(KillstreakUse use) => _activate(use);
    }

    static KillstreakFile Custom(string streaksJson) => KillstreakFile.Parse("{\"version\":1,\"streaks\":[" + streaksJson + "]}");

    const string Uav = "{\"id\":\"uav\",\"name\":\"UAV\",\"kills_required\":4,\"unlock_level\":0,\"duration_seconds\":60,\"control\":\"none\"}";
    const string Missile = "{\"id\":\"attack_missile\",\"name\":\"Missile\",\"kills_required\":5,\"unlock_level\":4,\"control\":\"pov\"}";

    // ---- killstreaks.json ------------------------------------------------------------------------------------------

    [Fact]
    public void ShippedTableHoldsTheEightAgreedStreaksWithTheirRules()
    {
        Assert.Equal(8, Shipping.Streaks.Count);
        var uav = Shipping.Find("uav")!;
        Assert.Equal(0, uav.UnlockLevel);
        Assert.Equal(60, uav.DurationSeconds);
        Assert.Equal(KillstreakControl.Pov, Shipping.Find("attack_missile")!.Control);
        Assert.Equal(KillstreakControl.Pov, Shipping.Find("attack_helicopter")!.Control);
        Assert.Equal(KillstreakControl.Pov, Shipping.Find("ac130")!.Control);
        Assert.Equal(KillstreakControl.None, Shipping.Find("moab")!.Control);
        Assert.All(Shipping.Streaks, s => Assert.Equal(1, s.MaxActive));
    }

    [Fact]
    public void StarterStreakIsRequiredAndOpenAtLevelZero()
    {
        Assert.Contains("starter streak \"uav\" is missing", Assert.Throws<DataFileException>(() => Custom(Missile)).Message);
        var locked = "{\"id\":\"uav\",\"name\":\"UAV\",\"kills_required\":4,\"unlock_level\":2,\"control\":\"none\"}";
        Assert.Contains("must have unlock_level 0", Assert.Throws<DataFileException>(() => Custom(locked)).Message);
    }

    [Fact]
    public void FieldRulesFailAtLoadWithTheStreakNamed()
    {
        Assert.Contains("\"kills_required\" is required", Assert.Throws<DataFileException>(() =>
            Custom(Uav + ",{\"id\":\"x\",\"name\":\"X\",\"unlock_level\":1,\"control\":\"none\"}")).Message);
        Assert.Contains("\"control\" must be none or pov", Assert.Throws<DataFileException>(() =>
            Custom(Uav + ",{\"id\":\"x\",\"name\":\"X\",\"kills_required\":1,\"unlock_level\":1,\"control\":\"ai\"}")).Message);
        Assert.Contains("duplicate streak id \"uav\"", Assert.Throws<DataFileException>(() => Custom(Uav + "," + Uav)).Message);
        Assert.Contains("\"max_active\" must be at least 1", Assert.Throws<DataFileException>(() =>
            Custom(Uav + ",{\"id\":\"x\",\"name\":\"X\",\"kills_required\":1,\"unlock_level\":1,\"max_active\":0,\"control\":\"none\"}")).Message);
    }

    [Fact]
    public void LevelsFileRejectsAStreakLineThatDisagreesWithItsOwnLevel()
    {
        var shipped = File.ReadAllText(Path.Combine(DataDir, "levels.json"));
        var wrong = shipped.Replace("{ \"level\": 14, \"kind\": \"streak\", \"id\": \"ac130\" }", "{ \"level\": 15, \"kind\": \"streak\", \"id\": \"ac130\" }");
        Assert.NotEqual(shipped, wrong);
        Assert.Contains("make them match", Assert.Throws<DataFileException>(() =>
            LevelsFile.Parse(wrong, Shipped.Weapons, Shipped.Perks, Shipping.UnlockLevels)).Message);
    }

    // ---- counter, earning and reset --------------------------------------------------------------------------------

    [Fact]
    public void CounterCountsKillsAndAStreakIsEarnedAtItsThreshold()
    {
        var h = new Harness(Shipping);
        h.Service.Arm(Player, new[] { "uav", "", "" }, 0);
        Kills(h, Player, 3);
        Assert.Equal(3, h.Service.KillCount(Player));
        Assert.Empty(h.Service.Held(Player));
        Kills(h, Player, 1);
        Assert.Equal(new[] { "uav" }, h.Service.Held(Player));
        var earned = Assert.Single(h.Sent);
        Assert.Equal(EventKind.StreakEarned, earned.Kind);
        Assert.Equal(new StreakEarnedInfo("uav", 4), KillstreakEvents.DecodeEarned(earned.Payload));
    }

    [Fact]
    public void AStreakIsEarnedOnceAndNotAgainForLaterKills()
    {
        var h = new Harness(Shipping);
        h.Service.Arm(Player, new[] { "uav", "", "" }, 0);
        Kills(h, Player, 10);
        Assert.Single(h.Sent);
        Assert.Equal(10, h.Service.KillCount(Player));
    }

    [Fact]
    public void DeathResetsTheCounterAndWhatWasEarned()
    {
        var h = new Harness(Shipping);
        h.Service.Arm(Player, new[] { "uav", "", "" }, 0);
        Kills(h, Player, 4);
        h.Service.OnDeath(Player);
        Assert.Equal(0, h.Service.KillCount(Player));
        Assert.Empty(h.Service.Held(Player));
        Kills(h, Player, 3);
        Assert.Empty(h.Service.Held(Player)); // the count restarted: three is not enough
        Kills(h, Player, 1);
        Assert.Equal(new[] { "uav" }, h.Service.Held(Player)); // earned again in the new life
    }

    [Fact]
    public void SuicidesDoNotCount()
    {
        var h = new Harness(Shipping);
        h.Service.Arm(Player, new[] { "uav", "", "" }, 0);
        for (var i = 0; i < 6; i++) h.Service.OnKill(Player, Player, false);
        Assert.Equal(0, h.Service.KillCount(Player));
    }

    [Fact]
    public void KillsByAStreakWeaponDoNotAdvanceTheCounter()
    {
        var h = new Harness(Shipping);
        h.Service.Arm(Player, new[] { "uav", "", "" }, 0);
        Kills(h, Player, 10, fromStreak: true);
        Assert.Equal(0, h.Service.KillCount(Player));
        Assert.Empty(h.Service.Held(Player));
    }

    [Fact]
    public void HotStreakLowersEachThresholdByOneKillButNotBelowOne()
    {
        var h = new Harness(Shipping);
        h.Service.Arm(Player, new[] { "uav", "attack_missile", "" }, 1f);
        Kills(h, Player, 3);
        Assert.Equal(new[] { "uav" }, h.Service.Held(Player));                    // uav at 4 - 1 = 3
        Kills(h, Player, 1);
        Assert.Equal(new[] { "uav", "attack_missile" }, h.Service.Held(Player));  // missile at 5 - 1 = 4
        Assert.Equal(1, KillstreakService.Threshold(new KillstreakDef { KillsRequired = 1 }, 1f));
        Assert.Equal(3, KillstreakService.Threshold(Shipping.Find("uav")!, 1f));
        Assert.Equal(4, KillstreakService.Threshold(Shipping.Find("uav")!, 0f));
    }

    [Fact]
    public void HeldStreaksKeepLoadoutOrder()
    {
        var h = new Harness(Shipping);
        h.Service.Arm(Player, new[] { "attack_missile", "uav", "" }, 0);
        Kills(h, Player, 5);
        Assert.Equal(new[] { "attack_missile", "uav" }, h.Service.Held(Player));
    }

    // ---- activation, duration, end ---------------------------------------------------------------------------------

    [Fact]
    public void ActivationUsesAHeldStreakOnce()
    {
        var h = new Harness(Shipping);
        h.Service.Arm(Player, new[] { "uav", "", "" }, 0);
        Assert.False(h.Service.Activate(Player, 0, Here)); // nothing held yet
        Kills(h, Player, 4);
        Assert.True(h.Service.Activate(Player, 1, Here));
        Assert.Empty(h.Service.Held(Player));
        Assert.Equal(1, h.Service.ActiveCount(Player, "uav"));
        Assert.Contains(h.Sent, s => s.Kind == EventKind.StreakUsed && KillstreakEvents.DecodeUsed(s.Payload)!.StreakId == "uav");
        Assert.False(h.Service.Activate(Player, 2, Here)); // a used streak is not held again this life
    }

    [Fact]
    public void DifferentStreaksCanBeActiveTogether()
    {
        var h = new Harness(Shipping);
        h.Service.Arm(Player, new[] { "uav", "turret", "" }, 0);
        Kills(h, Player, 6);
        Assert.True(h.Service.Activate(Player, 0, Here)); // uav
        Assert.Equal(new[] { "turret" }, h.Service.Held(Player));
        Assert.True(h.Service.Activate(Player, 0, Here)); // turret, beside uav
        Assert.Equal(1, h.Service.ActiveCount(Player, "uav"));
        Assert.Equal(1, h.Service.ActiveCount(Player, "turret"));
    }

    [Fact]
    public void RefusedActivationLeavesTheStreakHeld()
    {
        var h = new Harness(Shipping, With("uav", new ScriptedStreak("uav", _ => false)));
        h.Service.Arm(Player, new[] { "uav", "", "" }, 0);
        Kills(h, Player, 4);
        Assert.False(h.Service.Activate(Player, 0, Here));
        Assert.Equal(new[] { "uav" }, h.Service.Held(Player));
        Assert.Equal(0, h.Service.ActiveCount(Player, "uav"));
        Assert.DoesNotContain(h.Sent, s => s.Kind == EventKind.StreakUsed);
    }

    [Fact]
    public void DurationEndsAnInstanceAndSendsTheReason()
    {
        var h = new Harness(Shipping);
        h.Service.Arm(Player, new[] { "uav", "", "" }, 0);
        Kills(h, Player, 4);
        h.Service.Activate(Player, 10, Here);
        h.Service.Tick(69.9);
        Assert.Equal(1, h.Service.ActiveCount(Player, "uav"));
        h.Service.Tick(70);
        Assert.Equal(0, h.Service.ActiveCount(Player, "uav"));
        Assert.Equal(new StreakEndedInfo("uav", KillstreakEndReason.Expired), KillstreakEvents.DecodeEnded(h.Sent.Last().Payload));
    }

    [Fact]
    public void DeathEndsEveryInstanceOfTheOwner()
    {
        var h = new Harness(Shipping);
        h.Service.Arm(Player, new[] { "uav", "turret", "" }, 0);
        Kills(h, Player, 6);
        h.Service.Activate(Player, 0, Here);
        h.Service.Activate(Player, 0, Here);
        h.Service.OnDeath(Player);
        Assert.Equal(0, h.Service.ActiveCount(Player, "uav") + h.Service.ActiveCount(Player, "turret"));
        Assert.Contains(h.Sent, s => s.Kind == EventKind.StreakEnded && KillstreakEvents.DecodeEnded(s.Payload)!.Reason == KillstreakEndReason.OwnerDied);
    }

    [Fact]
    public void AStreakThatEndsItselfDuringActivationSendsUsedThenEnded()
    {
        var instant = new ScriptedStreak("uav", use => { use.End(); return true; });
        var h = new Harness(Shipping, With("uav", instant));
        h.Service.Arm(Player, new[] { "uav", "", "" }, 0);
        Kills(h, Player, 4);
        Assert.True(h.Service.Activate(Player, 0, Here));
        Assert.Equal(new[] { EventKind.StreakEarned, EventKind.StreakUsed, EventKind.StreakEnded }, h.KindsTo(Player));
        Assert.Equal(0, h.Service.ActiveCount(Player, "uav"));
    }

    [Fact]
    public void EndCallbacksRunOnceWhenTheInstanceEnds()
    {
        var calls = 0;
        KillstreakEndReason? seen = null;
        var streak = new ScriptedStreak("uav", use =>
        {
            use.OnEnded(r => { calls++; seen = r; });
            return true;
        });
        var h = new Harness(Shipping, With("uav", streak));
        h.Service.Arm(Player, new[] { "uav", "", "" }, 0);
        Kills(h, Player, 4);
        h.Service.Activate(Player, 0, Here);
        h.Service.OnDeath(Player);
        h.Service.OnDeath(Player);
        Assert.Equal(1, calls);
        Assert.Equal(KillstreakEndReason.OwnerDied, seen);
    }

    [Fact]
    public void ActivationWithoutAnAliveLifeIsRefused()
    {
        var h = new Harness(Shipping);
        Assert.False(h.Service.Activate(Enemy, 0, Here));
    }

    // ---- point-of-view control -------------------------------------------------------------------------------------

    [Fact]
    public void ControlSessionRoutesInputUntilTheStreakEnds()
    {
        KillstreakUse? held = null;
        var missile = new ScriptedStreak("attack_missile", use =>
        {
            held = use;
            return use.TakeControl(42, _ => { });
        });
        var h = new Harness(Shipping, With("attack_missile", missile));
        h.Service.Arm(Player, new[] { "attack_missile", "", "" }, 0);
        Kills(h, Player, 5);
        Assert.True(h.Service.Activate(Player, 0, Here));
        Assert.Equal((ushort)42, h.Service.ControlOf(Player)!.EntityId);
        Assert.True(h.Service.RouteInput(Player, new NetInput()));
        held!.End();
        Assert.Null(h.Service.ControlOf(Player));
        Assert.False(h.Service.RouteInput(Player, new NetInput()));
    }

    [Fact]
    public void AnOwnerControlsOneEntityAtATime()
    {
        var secondTaken = true;
        var missile = new ScriptedStreak("attack_missile", use => use.TakeControl(1, _ => { }));
        var turret = new ScriptedStreak("turret", use => { secondTaken = use.TakeControl(2, _ => { }); return true; });
        var h = new Harness(Shipping, With("attack_missile", missile).Select(i => i.Id == "turret" ? turret : i));
        h.Service.Arm(Player, new[] { "attack_missile", "turret", "" }, 0);
        Kills(h, Player, 6);
        Assert.True(h.Service.Activate(Player, 0, Here));
        Assert.True(h.Service.Activate(Player, 0, Here));
        Assert.False(secondTaken);
        Assert.Equal((ushort)1, h.Service.ControlOf(Player)!.EntityId);
    }

    // ---- ownership, rounds, leaving --------------------------------------------------------------------------------

    [Fact]
    public void IsStreakWeaponFollowsTheImplementations()
    {
        var turret = new ScriptedStreak("turret", _ => true, new HashSet<string> { "turret_gun" });
        var h = new Harness(Shipping, With("turret", turret));
        Assert.True(h.Service.IsStreakWeapon("turret_gun"));
        Assert.False(h.Service.IsStreakWeapon("ak47"));
    }

    [Fact]
    public void RoundResetEndsEverythingAndClearsTheCounters()
    {
        var h = new Harness(Shipping);
        h.Service.Arm(Player, new[] { "uav", "", "" }, 0);
        Kills(h, Player, 4);
        h.Service.Activate(Player, 0, Here);
        h.Service.Reset();
        Assert.Equal(0, h.Service.ActiveCount(Player, "uav"));
        Assert.Equal(0, h.Service.KillCount(Player));
        Assert.Contains(h.Sent, s => s.Kind == EventKind.StreakEnded && KillstreakEvents.DecodeEnded(s.Payload)!.Reason == KillstreakEndReason.RoundReset);
    }

    [Fact]
    public void LeavingEndsTheOwnersStreaks()
    {
        var h = new Harness(Shipping);
        h.Service.Arm(Player, new[] { "uav", "", "" }, 0);
        Kills(h, Player, 4);
        h.Service.Activate(Player, 0, Here);
        h.Service.Forget(Player);
        Assert.Equal(0, h.Service.ActiveCount(Player, "uav"));
        Assert.Empty(h.Service.Held(Player));
    }

    [Fact]
    public void OnlyTheKillerAdvancesTheirOwnCounter()
    {
        var h = new Harness(Shipping);
        h.Service.Arm(Player, new[] { "uav", "", "" }, 0);
        h.Service.Arm(Enemy, new[] { "uav", "", "" }, 0);
        Kills(h, Player, 2);
        Assert.Equal(2, h.Service.KillCount(Player));
        Assert.Equal(0, h.Service.KillCount(Enemy));
    }

    // ---- construction and events ------------------------------------------------------------------------------------

    [Fact]
    public void ServiceNeedsExactlyOneImplementationPerStreak()
    {
        var missing = Shipping.Streaks.Where(s => s.Id != "moab").Select(s => (IKillstreak)new LoggingKillstreak(s.Id));
        Assert.Contains("no killstreak implementation for: moab", Assert.Throws<InvalidOperationException>(() =>
            new KillstreakService(Shipping, missing, (_, _, _) => { }, _ => { })).Message);
        var extra = Shipping.Streaks.Select(s => (IKillstreak)new LoggingKillstreak(s.Id)).Append(new LoggingKillstreak("nuke"));
        Assert.Contains("not in killstreaks.json: nuke", Assert.Throws<InvalidOperationException>(() =>
            new KillstreakService(Shipping, extra, (_, _, _) => { }, _ => { })).Message);
    }

    [Fact]
    public void EventPayloadsRoundTrip()
    {
        Assert.Equal(new StreakEarnedInfo("ac130", 18), KillstreakEvents.DecodeEarned(KillstreakEvents.EncodeEarned("ac130", 18)));
        Assert.Equal(new StreakUsedInfo("moab"), KillstreakEvents.DecodeUsed(KillstreakEvents.EncodeUsed("moab")));
        Assert.Equal(new StreakEndedInfo("uav", KillstreakEndReason.OwnerLeft), KillstreakEvents.DecodeEnded(KillstreakEvents.EncodeEnded("uav", KillstreakEndReason.OwnerLeft)));
        Assert.Null(KillstreakEvents.DecodeEnded(new byte[] { 3, (byte)'u', (byte)'a', (byte)'v' }));
        Assert.Null(KillstreakEvents.DecodeEarned(new byte[] { 9, 1 }));
    }

    [Fact]
    public void EventKindsAreTheNextFreeWireValues()
    {
        Assert.Equal(17, (int)EventKind.StreakEarned);
        Assert.Equal(18, (int)EventKind.StreakUsed);
        Assert.Equal(19, (int)EventKind.StreakEnded);
    }

    [Fact]
    public void KillstreakButtonBitDoesNotOverlapTheOthers()
    {
        Assert.Equal(1024, (int)MoveButtons.Killstreak);
        var others = MoveButtons.Jump | MoveButtons.Sprint | MoveButtons.Crouch | MoveButtons.Fire | MoveButtons.Aim | MoveButtons.Reload
                     | MoveButtons.Use | MoveButtons.Switch | MoveButtons.Headlights | MoveButtons.Prone;
        Assert.Equal(0, (int)(others & MoveButtons.Killstreak));
    }
}

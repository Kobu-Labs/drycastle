using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Ticket #129: the MOAB countdown, the one-per-match gate, the cancel paths, its victims and its event codec.</summary>
public class MoabTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);

    const ushort Owner = 7, Other = 9, Enemy = 8;
    static readonly Vector3 Here = new(1, 0, 2);

    sealed class Harness
    {
        public readonly List<MoabPhase> Phases = new();
        public readonly List<(ushort Owner, EventKind Kind, byte[] Payload)> Sent = new();
        public readonly MoabStreak Moab;
        public readonly KillstreakService Service;

        public Harness()
        {
            Moab = new MoabStreak((phase, _) => Phases.Add(phase));
            var impl = Shipped.Killstreaks.Streaks.Select(s => s.Id == MoabStreak.StreakId ? (IKillstreak)Moab : new LoggingKillstreak(s.Id));
            Service = new KillstreakService(Shipped.Killstreaks, impl, (o, k, p) => Sent.Add((o, k, p)), _ => { });
        }

        /// <summary>Gives the player a life with a MOAB in the loadout and enough kills to have earned it.</summary>
        public void Earn(ushort player)
        {
            Service.Arm(player, new[] { MoabStreak.StreakId }, 0);
            for (var i = 0; i < 24; i++) Service.OnKill(player, Enemy, fromStreak: false);
        }
    }

    [Fact]
    public void ShippedMoabCountsDownFiveSeconds()
    {
        Assert.Equal(5, Shipped.Killstreaks.Find("moab")!.DurationSeconds);
    }

    [Fact]
    public void ActivationArmsTheCountdownAndDetonatesWhenItRunsOut()
    {
        var h = new Harness();
        h.Earn(Owner);

        Assert.True(h.Service.Activate(Owner, 100, Here));
        Assert.Equal(new[] { MoabPhase.Armed }, h.Phases);
        Assert.True(h.Moab.Pending);

        h.Service.Tick(104.9);
        Assert.Equal(new[] { MoabPhase.Armed }, h.Phases);

        h.Service.Tick(105.0);
        Assert.Equal(new[] { MoabPhase.Armed, MoabPhase.Detonated }, h.Phases);
        Assert.False(h.Moab.Pending);
    }

    [Fact]
    public void OnlyOneMoabIsPendingPerMatch()
    {
        var h = new Harness();
        h.Earn(Owner);
        h.Earn(Other);

        Assert.True(h.Service.Activate(Owner, 0, Here));
        Assert.False(h.Service.Activate(Other, 1, Here));          // refused: the first is still counting down
        Assert.Equal(new[] { "moab" }, h.Service.Held(Other));     // the second streak stays held
        Assert.Equal(0, h.Service.ActiveCount(Other, "moab"));

        h.Service.Tick(5);                                          // the first detonates and frees the gate
        Assert.True(h.Service.Activate(Other, 6, Here));
        Assert.Equal(MoabPhase.Armed, h.Phases[^1]);
    }

    [Fact]
    public void OwnerDyingDuringCountdownCancelsWithoutDetonating()
    {
        var h = new Harness();
        h.Earn(Owner);
        h.Service.Activate(Owner, 0, Here);

        h.Service.OnDeath(Owner);

        Assert.Equal(new[] { MoabPhase.Armed, MoabPhase.Cancelled }, h.Phases);
        Assert.False(h.Moab.Pending);
    }

    [Fact]
    public void RoundResetDuringCountdownCancelsWithoutDetonating()
    {
        var h = new Harness();
        h.Earn(Owner);
        h.Service.Activate(Owner, 0, Here);

        h.Service.Reset();

        Assert.Equal(new[] { MoabPhase.Armed, MoabPhase.Cancelled }, h.Phases);
        Assert.False(h.Moab.Pending);
    }

    [Fact]
    public void LeavingDuringCountdownCancels()
    {
        var h = new Harness();
        h.Earn(Owner);
        h.Service.Activate(Owner, 0, Here);

        h.Service.Forget(Owner);

        Assert.Equal(MoabPhase.Cancelled, h.Phases[^1]);
    }

    [Fact]
    public void MoabKillsAreStreakKills()
    {
        var h = new Harness();
        Assert.True(h.Service.IsStreakWeapon(MoabStreak.OwnedWeapon));
        Assert.Contains(MoabStreak.OwnedWeapon, h.Moab.OwnedWeapons);
    }

    [Fact]
    public void VictimsAreEveryoneAliveButTheOwner()
    {
        var alive = new ushort[] { 3, Owner, 4, 5 };
        Assert.Equal(new ushort[] { 3, 4, 5 }, MoabStreak.Victims(alive, Owner).ToArray());
        Assert.Empty(MoabStreak.Victims(new ushort[] { Owner }, Owner));
    }

    [Fact]
    public void EventRoundTripsEachPhase()
    {
        foreach (var phase in Enum.GetValues<MoabPhase>())
        {
            var m = new MoabInfo(phase, 42, new Vector3(1.5f, -2f, 300f), 0xDEADBEEF, 5f);
            var bytes = MoabEvents.Encode(m);
            Assert.Equal(MoabEvents.Size, bytes.Length);
            Assert.Equal(m, MoabEvents.Decode(bytes));
        }
    }

    [Fact]
    public void MalformedEventIsRejected()
    {
        Assert.Null(MoabEvents.Decode(new byte[MoabEvents.Size - 1]));
        var bad = MoabEvents.Encode(new MoabInfo(MoabPhase.Armed, 1, Vector3.Zero, 0, 5));
        bad[0] = 9;
        Assert.Null(MoabEvents.Decode(bad));
    }

    [Fact]
    public void MoabHasItsOwnEventKindInTheReservedRange()
    {
        Assert.Equal(70, (int)EventKind.Moab);
    }
}

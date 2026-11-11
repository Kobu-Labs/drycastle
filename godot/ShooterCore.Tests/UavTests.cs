using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Issue #121: the UAV streak's visibility rule, its once-a-second pulse to the owner alone, and its codec.</summary>
public class UavTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);

    const ushort Owner = 7, Enemy = 8, Ghost = 9;
    static readonly Vector3 Here = new(1, 0, 2);

    sealed class Harness
    {
        public readonly List<(ushort Owner, EventKind Kind, byte[] Payload)> Sent = new();
        public readonly List<UavContact> Contacts = new();
        public readonly KillstreakService Service;
        public readonly UavStreak Uav;

        public Harness()
        {
            Uav = new UavStreak(() => Contacts, (o, k, p) => Sent.Add((o, k, p)));
            var impl = Shipped.Killstreaks.Streaks.Select(s => s.Id == UavStreak.StreakId ? (IKillstreak)Uav : new LoggingKillstreak(s.Id));
            Service = new KillstreakService(Shipped.Killstreaks, impl, (o, k, p) => Sent.Add((o, k, p)), _ => { });
            Service.Arm(Owner, new[] { UavStreak.StreakId }, 0);
            for (var i = 0; i < 4; i++) Service.OnKill(Owner, Enemy, fromStreak: false);
            Contacts.Add(new UavContact(Owner, 0, 0, 1));
            Contacts.Add(new UavContact(Enemy, 10, -20, 1));
            Contacts.Add(new UavContact(Ghost, 30, 40, 0));
        }

        /// <summary>What the server does each frame: the service ends expired streaks, then the UAV sends its due pulses.</summary>
        public void Advance(double now)
        {
            Service.Tick(now);
            Uav.Tick(now);
        }

        public List<UavPulseInfo> Pulses(ushort to) =>
            Sent.Where(s => s.Kind == EventKind.UavPing && s.Owner == to).Select(s => UavEvents.Decode(s.Payload)!).ToList();
    }

    [Fact]
    public void ShippedUavRunsOneMinute()
    {
        Assert.Equal(60, Shipped.Killstreaks.Find(UavStreak.StreakId)!.DurationSeconds);
    }

    [Fact]
    public void VisibleIsEveryoneButTheOwnerAndTheGhost()
    {
        var all = new[] { new UavContact(Owner, 0, 0, 1), new UavContact(Enemy, 10, -20, 1), new UavContact(Ghost, 30, 40, 0), new UavContact(12, 5, 6, 1) };
        var shown = UavRules.Visible(all, Owner);
        Assert.Equal(new[] { new System.Numerics.Vector2(10, -20), new System.Numerics.Vector2(5, 6) }, shown);
    }

    [Fact]
    public void ActivationPulsesAtOnceToTheOwnerOnly()
    {
        var h = new Harness();
        Assert.True(h.Service.Activate(Owner, 100, Here));

        var pulses = h.Pulses(Owner);
        Assert.Single(pulses);
        Assert.Equal(60f, pulses[0].SecondsLeft, 3);
        Assert.Equal(new[] { new Vector2(10, -20) }, pulses[0].Contacts);
        Assert.Empty(h.Sent.Where(s => s.Kind == EventKind.UavPing && s.Owner != Owner));
    }

    [Fact]
    public void PulsesEveryOneSecondWithTimeLeftUntilItExpires()
    {
        var h = new Harness();
        h.Service.Activate(Owner, 100, Here);

        h.Advance(100.5);
        h.Advance(100.99);
        Assert.Single(h.Pulses(Owner));

        h.Advance(101.0);
        Assert.Equal(2, h.Pulses(Owner).Count);
        Assert.Equal(59f, h.Pulses(Owner)[1].SecondsLeft, 3);

        h.Advance(159.0);
        var last = h.Pulses(Owner).Last();
        Assert.Equal(1f, last.SecondsLeft, 3);

        var before = h.Sent.Count;
        h.Advance(160.0); // duration runs out: the service ends the streak, no more pulses
        Assert.Equal(before + 1, h.Sent.Count); // only the StreakEnded event
        Assert.Equal(EventKind.StreakEnded, h.Sent.Last().Kind);
        h.Advance(170.0);
        Assert.Equal(before + 1, h.Sent.Count);
    }

    [Fact]
    public void GhostIsLeftOutOfThePulse()
    {
        var h = new Harness();
        h.Service.Activate(Owner, 100, Here);
        Assert.DoesNotContain(h.Pulses(Owner)[0].Contacts, p => p == new Vector2(30, 40));
    }

    [Fact]
    public void OwnersDeathEndsThePulses()
    {
        var h = new Harness();
        h.Service.Activate(Owner, 100, Here);
        h.Service.OnDeath(Owner);

        var count = h.Pulses(Owner).Count;
        h.Advance(105.0);
        Assert.Equal(count, h.Pulses(Owner).Count);
        Assert.Equal(0, h.Service.ActiveCount(Owner, UavStreak.StreakId));
    }

    [Fact]
    public void SecondUseIsRefusedWhileOneIsUp()
    {
        var h = new Harness();
        Assert.True(h.Service.Activate(Owner, 100, Here));
        // the streak is spent: nothing else is held, so a second activation does nothing
        Assert.False(h.Service.Activate(Owner, 101, Here));
        Assert.Single(h.Pulses(Owner).Where(p => p.SecondsLeft > 58.5f && p.SecondsLeft <= 60));
    }

    [Fact]
    public void PulseCodecRoundTrips()
    {
        var info = new UavPulseInfo(42.5f, new List<Vector2> { new(1.5f, -2f), new(-300f, 99.25f) });
        var back = UavEvents.Decode(UavEvents.Encode(info))!;
        Assert.Equal(42.5f, back.SecondsLeft);
        Assert.Equal(info.Contacts, back.Contacts);
    }

    [Fact]
    public void PulseCodecRejectsShortAndTruncatedPayloads()
    {
        Assert.Null(UavEvents.Decode(new byte[5]));
        var full = UavEvents.Encode(new UavPulseInfo(1f, new List<Vector2> { new(1, 2) }));
        Assert.Null(UavEvents.Decode(full.AsSpan(0, full.Length - 1)));
    }

    [Fact]
    public void UavPingKindIsNinetyAndAboveTheOtherStreakKinds()
    {
        Assert.Equal(90, (int)EventKind.UavPing);
        Assert.True((int)EventKind.UavPing > (int)EventKind.StreakEnded);
    }
}

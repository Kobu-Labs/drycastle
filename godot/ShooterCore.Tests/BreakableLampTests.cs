using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Shootable pole lamps (ADR 0038, #337): the state machine, the hit geometry, the wire, the pool and the flicker.</summary>
public class BreakableLampTests
{
    static readonly LampLook Look = new() { RespawnSeconds = 120, FlickerSeconds = 3, HitRadiusM = 0.25 };

    static BreakableLamps Lamps(int n = 3) =>
        new(Enumerable.Range(0, n).Select(i => new Vector3(i * 10f, 5f, 0)).ToList(), Look);

    [Fact]
    public void AShotBreaksALampAndASecondShotChangesNothing()
    {
        var l = Lamps();
        Assert.True(l.Shoot(1));
        Assert.Equal(LampState.Broken, l.StateOf(1));
        Assert.Equal(LampState.Lit, l.StateOf(0));
        Assert.False(l.Shoot(1));
        Assert.Equal(new[] { new LampStateMsg(1, LampState.Broken) }, l.TakeChanges());
        Assert.Empty(l.TakeChanges());
        Assert.False(l.Shoot(7)); // no such lamp
    }

    [Fact]
    public void ABrokenLampFlickersAfterTheRespawnTimeThenIsLit()
    {
        var l = Lamps();
        l.Shoot(0); l.TakeChanges();
        l.Tick(119.9f);
        Assert.Equal(LampState.Broken, l.StateOf(0));
        l.Tick(0.2f);
        Assert.Equal(LampState.Flickering, l.StateOf(0));
        l.Tick(2.9f);
        Assert.Equal(LampState.Flickering, l.StateOf(0));
        l.Tick(0.2f);
        Assert.Equal(LampState.Lit, l.StateOf(0));
        Assert.Equal(new[] { new LampStateMsg(0, LampState.Flickering), new LampStateMsg(0, LampState.Lit) }, l.TakeChanges());
        Assert.Empty(l.Unlit());
    }

    [Fact]
    public void AFlickeringLampBreaksAgainWithAFreshClock()
    {
        var l = Lamps();
        l.Shoot(0); l.Tick(121f);
        Assert.Equal(LampState.Flickering, l.StateOf(0));
        Assert.True(l.Shoot(0));
        l.Tick(119f);
        Assert.Equal(LampState.Broken, l.StateOf(0));
        l.Tick(2f);
        Assert.Equal(LampState.Flickering, l.StateOf(0));
    }

    [Fact]
    public void ANewRoundLightsEveryLampWithoutATransition()
    {
        var l = Lamps();
        l.Shoot(0); l.Shoot(2);
        Assert.Equal(2, l.Unlit().Count);
        Assert.True(l.RestoreAll());
        Assert.Empty(l.Unlit());
        Assert.Empty(l.TakeChanges());
        Assert.False(l.RestoreAll());
        l.Tick(500f); // nothing left to count down
        Assert.Empty(l.TakeChanges());
    }

    [Fact]
    public void ARoundBreaksEveryLampItPassesWithinTheHitRadiusAndNoOther()
    {
        var l = Lamps(); // heads at x = 0, 10, 20, y = 5
        var dir = Vector3.UnitX;
        Assert.Equal(2, l.ShootAlong(new Vector3(-5, 5.2f, 0), dir, 20f)); // passes lamps 0 and 1, stops before 2
        Assert.Equal(LampState.Lit, l.StateOf(2));
        Assert.Equal(0, l.ShootAlong(new Vector3(-5, 6f, 0), dir, 40f)); // a metre above: a miss
        Assert.Equal(1, l.ShootAlong(new Vector3(-5, 5f, 0), dir, 40f)); // lamps 0 and 1 are broken already
    }

    [Fact]
    public void TheHitIsTheSegmentAgainstTheSphere()
    {
        var c = new Vector3(0, 5, 10);
        Assert.True(LampGeometry.Hits(new Vector3(0, 5, 0), Vector3.UnitZ, 20f, c, 0.25f)); // straight through
        Assert.True(LampGeometry.Hits(new Vector3(0.24f, 5, 0), Vector3.UnitZ, 20f, c, 0.25f)); // grazing
        Assert.False(LampGeometry.Hits(new Vector3(0.3f, 5, 0), Vector3.UnitZ, 20f, c, 0.25f));
        Assert.False(LampGeometry.Hits(new Vector3(0, 5, 0), Vector3.UnitZ, 9.5f, c, 0.25f)); // the round stopped short of it
        Assert.True(LampGeometry.Hits(new Vector3(0, 5, 0), Vector3.UnitZ, 9.8f, c, 0.25f));
        Assert.False(LampGeometry.Hits(new Vector3(0, 5, 20), Vector3.UnitZ, 20f, c, 0.25f)); // flying away from it
        Assert.False(LampGeometry.Hits(new Vector3(0, 5, 0), Vector3.UnitZ, 0f, c, 0.25f));
    }

    [Fact]
    public void TheWireRoundTripsAndRefusesBadBytes()
    {
        var m = new LampStateMsg(513, LampState.Flickering);
        Assert.Equal(m, LampCodec.DecodeState(LampCodec.EncodeState(m)));
        Assert.Null(LampCodec.DecodeState(new byte[2]));
        Assert.Null(LampCodec.DecodeState(new byte[] { 1, 0, 9 })); // no such state
        var list = new[] { new LampStateMsg(2, LampState.Broken), new LampStateMsg(40, LampState.Flickering) };
        Assert.Equal(list, LampCodec.DecodeSync(LampCodec.EncodeSync(list)));
        Assert.Empty(LampCodec.DecodeSync(LampCodec.EncodeSync(Array.Empty<LampStateMsg>()))!);
        var bytes = LampCodec.EncodeSync(list);
        Assert.Null(LampCodec.DecodeSync(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Null(LampCodec.DecodeSync(new byte[1]));
        Assert.Equal(250, (int)EventKind.LampState);
        Assert.Equal(251, (int)EventKind.LampSync);
    }

    [Fact]
    public void AJoinerGetsTheLampsThatAreNotLit()
    {
        var server = Lamps(); server.Shoot(1); server.Shoot(2); server.Tick(121f); server.Shoot(2);
        var client = LampCodec.DecodeSync(LampCodec.EncodeSync(server.Unlit()))!;
        Assert.Equal(new[] { new LampStateMsg(1, LampState.Flickering), new LampStateMsg(2, LampState.Broken) }, client);
    }

    [Fact]
    public void ThePoolNeverPicksABrokenLamp()
    {
        var lamps = Enumerable.Range(0, 5).Select(i => new Vector3(i * 10f, 0, 0)).ToList();
        var slots = new int[2]; Array.Fill(slots, -1);
        var off = new bool[5]; off[0] = true; off[1] = true;
        LampPool.Refresh(slots, lamps, Vector3.Zero, 100f, off);
        Assert.Equal(new HashSet<int> { 2, 3 }, slots.ToHashSet());
        off[2] = true; // a lamp a slot held breaks: the slot moves on
        LampPool.Refresh(slots, lamps, Vector3.Zero, 100f, off);
        Assert.Equal(new HashSet<int> { 3, 4 }, slots.ToHashSet());
        Array.Fill(off, true);
        LampPool.Refresh(slots, lamps, Vector3.Zero, 100f, off);
        Assert.All(slots, s => Assert.Equal(-1, s));
    }

    [Fact]
    public void TheFlickerIsDeterministicStartsUnsteadyAndEndsOn()
    {
        for (var step = 0; step < 36; step++)
        {
            var age = step * LampFlicker.StepSeconds + 0.001f;
            Assert.Equal(LampFlicker.On(4, age, 3f), LampFlicker.On(4, age, 3f));
        }
        var ons = Enumerable.Range(0, 36).Count(s => LampFlicker.On(4, s * LampFlicker.StepSeconds + 0.001f, 3f));
        Assert.InRange(ons, 5, 35); // neither dark nor steady through it
        Assert.True(LampFlicker.On(4, 3f, 3f));
        Assert.True(LampFlicker.On(4, 10f, 3f));
        Assert.NotEqual(Enumerable.Range(0, 36).Select(s => LampFlicker.On(1, s * LampFlicker.StepSeconds + 0.001f, 3f)),
                        Enumerable.Range(0, 36).Select(s => LampFlicker.On(2, s * LampFlicker.StepSeconds + 0.001f, 3f))); // lamps do not blink in step
    }

    [Fact]
    public void TheLampMachineIsInTheCatalogAndTheShippedDataLoads()
    {
        Assert.Contains(StateMachineCatalog.All, s => s.Name == "Lamp.Light");
        Assert.Empty(BreakableLamps.Spec.Unreachable);
        var c = EnvironmentConfig.Load(Path.Combine(AppContext.BaseDirectory, "data", "environment.json"));
        Assert.Equal(120f, c.Lamp.RespawnSeconds);
        Assert.Equal(3f, c.Lamp.FlickerSeconds);
        Assert.Equal(0.25, c.Lamp.HitRadiusM);
        Assert.Throws<DataFileException>(() => EnvironmentConfig.Parse("{\"version\":1,\"lamp\":{\"respawn_seconds\":0}}"));
    }
}

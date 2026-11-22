using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Shootable road sign plates (ADR 0038, #425): the state machine, the hit geometry, the wire, the join catch-up and the bend.</summary>
public class BreakableSignTests
{
    static readonly SignPlateLook Look = new() { RespawnSeconds = 120, HitRadiusM = 0.3 };

    static BreakableSigns Signs(int n = 3) =>
        new(Enumerable.Range(0, n).Select(i => new Vector3(i * 10f, 2.15f, 0)).ToList(), Look);

    [Fact]
    public void AShotBendsAPlateAndASecondShotChangesNothing()
    {
        var s = Signs();
        Assert.True(s.Shoot(1));
        Assert.Equal(SignPlateState.Bent, s.StateOf(1));
        Assert.Equal(SignPlateState.Whole, s.StateOf(0));
        Assert.False(s.Shoot(1));
        Assert.Equal(new[] { new SignPlateMsg(1, SignPlateState.Bent) }, s.TakeChanges());
        Assert.Empty(s.TakeChanges());
        Assert.False(s.Shoot(7)); // no such plate
    }

    [Fact]
    public void ABentPlateIsWholeAgainAfterTheRespawnTime()
    {
        var s = Signs();
        s.Shoot(0); s.TakeChanges();
        s.Tick(119.9f);
        Assert.Equal(SignPlateState.Bent, s.StateOf(0));
        s.Tick(0.2f);
        Assert.Equal(SignPlateState.Whole, s.StateOf(0));
        Assert.Equal(new[] { new SignPlateMsg(0, SignPlateState.Whole) }, s.TakeChanges());
        Assert.Empty(s.Bent());
        Assert.True(s.Shoot(0)); // and it can be shot again
    }

    [Fact]
    public void ASecondShotDoesNotRestartTheClock()
    {
        var s = Signs();
        s.Shoot(0);
        s.Tick(100f);
        Assert.False(s.Shoot(0));
        s.Tick(21f);
        Assert.Equal(SignPlateState.Whole, s.StateOf(0));
    }

    [Fact]
    public void ANewRoundStraightensEveryPlateWithoutATransition()
    {
        var s = Signs();
        s.Shoot(0); s.Shoot(2);
        Assert.Equal(2, s.Bent().Count);
        Assert.True(s.RestoreAll());
        Assert.Empty(s.Bent());
        Assert.Empty(s.TakeChanges());
        Assert.False(s.RestoreAll());
        s.Tick(500f); // nothing left to count down
        Assert.Empty(s.TakeChanges());
    }

    [Fact]
    public void ARoundBendsEveryPlateItPassesWithinTheHitRadiusAndNoOther()
    {
        var s = Signs(); // plates at x = 0, 10, 20, y = 2.15
        var dir = Vector3.UnitX;
        Assert.Equal(2, s.ShootAlong(new Vector3(-5, 2.3f, 0), dir, 20f)); // passes plates 0 and 1, stops before 2
        Assert.Equal(SignPlateState.Whole, s.StateOf(2));
        Assert.Equal(0, s.ShootAlong(new Vector3(-5, 3f, 0), dir, 40f)); // 85 cm above: a miss
        Assert.Equal(1, s.ShootAlong(new Vector3(-5, 2.15f, 0), dir, 40f)); // plates 0 and 1 are bent already
    }

    [Fact]
    public void TheHitIsTheSegmentAgainstTheSphere()
    {
        var c = new Vector3(0, 2.15f, 10);
        Assert.True(LampGeometry.Hits(new Vector3(0, 2.15f, 0), Vector3.UnitZ, 20f, c, 0.3f));
        Assert.True(LampGeometry.Hits(new Vector3(0.29f, 2.15f, 0), Vector3.UnitZ, 20f, c, 0.3f));
        Assert.False(LampGeometry.Hits(new Vector3(0.35f, 2.15f, 0), Vector3.UnitZ, 20f, c, 0.3f));
        Assert.False(LampGeometry.Hits(new Vector3(0, 2.15f, 0), Vector3.UnitZ, 9.5f, c, 0.3f)); // the round stopped short of it
    }

    [Fact]
    public void TheWireRoundTripsAndRefusesBadBytes()
    {
        var m = new SignPlateMsg(513, SignPlateState.Bent);
        Assert.Equal(m, SignPlateCodec.DecodeState(SignPlateCodec.EncodeState(m)));
        Assert.Null(SignPlateCodec.DecodeState(new byte[2]));
        Assert.Null(SignPlateCodec.DecodeState(new byte[] { 1, 0, 9 })); // no such state
        var list = new[] { new SignPlateMsg(2, SignPlateState.Bent), new SignPlateMsg(40, SignPlateState.Whole) };
        Assert.Equal(list, SignPlateCodec.DecodeSync(SignPlateCodec.EncodeSync(list)));
        Assert.Empty(SignPlateCodec.DecodeSync(SignPlateCodec.EncodeSync(Array.Empty<SignPlateMsg>()))!);
        var bytes = SignPlateCodec.EncodeSync(list);
        Assert.Null(SignPlateCodec.DecodeSync(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Null(SignPlateCodec.DecodeSync(new byte[1]));
        Assert.Equal(252, (int)EventKind.SignPlateState);
        Assert.Equal(253, (int)EventKind.SignPlateSync);
        Assert.True(Wire.ProtocolVersion >= 77);
        var back = Assert.IsType<EventMsg>(Wire.DecodeReliable(Wire.Encode(new EventMsg((byte)EventKind.SignPlateState, SignPlateCodec.EncodeState(m)))));
        Assert.Equal((byte)EventKind.SignPlateState, back.Kind);
        Assert.False(Wire.IsEffect(EventKind.SignPlateState));
        // no other event kind shares a number with the signs'
        Assert.Equal(1, Enum.GetValues<EventKind>().Count(k => (int)k == 252));
        Assert.Equal(1, Enum.GetValues<EventKind>().Count(k => (int)k == 253));
    }

    [Fact]
    public void AJoinerGetsThePlatesThatAreNotWhole()
    {
        var server = Signs(); server.Shoot(1); server.Shoot(2); server.Tick(121f); server.Shoot(2);
        var client = SignPlateCodec.DecodeSync(SignPlateCodec.EncodeSync(server.Bent()))!;
        Assert.Equal(new[] { new SignPlateMsg(2, SignPlateState.Bent) }, client);
    }

    [Fact]
    public void TheBendIsDeterministicAndPlatesDoNotAllLeanOneWay()
    {
        for (var i = 0; i < 20; i++) Assert.Equal(SignBend.Side(i), SignBend.Side(i));
        var sides = Enumerable.Range(0, 40).Select(SignBend.Side).ToList();
        Assert.All(sides, s => Assert.True(s is 1 or -1));
        Assert.Contains(1, sides);
        Assert.Contains(-1, sides);
    }

    [Fact]
    public void TheSignMachineIsInTheCatalogAndTheShippedDataLoads()
    {
        Assert.Contains(StateMachineCatalog.All, s => s.Name == "Sign.Plate");
        Assert.Empty(BreakableSigns.Spec.Unreachable);
        var c = EnvironmentConfig.Load(Path.Combine(AppContext.BaseDirectory, "data", "environment.json"));
        Assert.Equal(120f, c.SignPlate.RespawnSeconds);
        Assert.Equal(0.3, c.SignPlate.HitRadiusM);
        Assert.Throws<DataFileException>(() => EnvironmentConfig.Parse("{\"version\":1,\"sign_plate\":{\"respawn_seconds\":0}}"));
        Assert.Throws<DataFileException>(() => EnvironmentConfig.Parse("{\"version\":1,\"sign_plate\":{\"hit_radius_m\":0}}"));
    }
}

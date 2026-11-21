using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>#345: the dust of a blast is a pure function of the blast and the time.</summary>
public class DustRulesTests
{
    static readonly Vector3 Origin = new(100, 20, -50);
    static DustBlast Rocket => new(Origin, 6f, 10.0);
    static DustBlast Vehicle => new(Origin, 9f, 10.0);

    [Fact]
    public void StandingInARocketBlastIsObscuredForAFewSecondsThenClears()
    {
        var eye = Origin + new Vector3(1, 1.6f, 0);
        var field = new DustField();
        Assert.True(field.Add(Rocket));
        Assert.Equal(0f, field.Veil(eye, 10.0));
        Assert.True(field.Veil(eye, 10.2) > 0.9f); // the blast's first moment blinds
        Assert.Equal(DustRules.CloudVeilShare, field.Veil(eye, 11.5), 2); // then the drawn cloud does the hiding: near things can be made out
        Assert.True(DustRules.CloudDensity(Rocket, eye, 11.5) > 0.95f); // the cloud itself is as thick as before
        Assert.InRange(DustRules.CloudSeconds(6f), 3f, 5f);
        var late = field.Veil(eye, 13.5);
        Assert.InRange(late, 0.001f, 0.5f); // thinning, not gone at a stroke
        Assert.Equal(0f, field.Veil(eye, 14.1));
    }

    [Fact]
    public void TheVeilNeverFullyBlinds()
    {
        var field = new DustField();
        field.Add(Rocket);
        field.Add(new DustBlast(Origin + new Vector3(8, 0, 0), 6f, 10.0));
        Assert.True(field.Veil(Origin + new Vector3(4, 1, 0), 10.5) <= DustRules.MaxVeil);
    }

    [Fact]
    public void TheCloudStaysLowAndLocal()
    {
        Assert.Equal(0f, DustRules.CloudDensity(Rocket, Origin + new Vector3(0, 30, 0), 10.5)); // a helicopter above
        Assert.Equal(0f, DustRules.CloudDensity(Rocket, Origin + new Vector3(30, 1, 0), 10.5)); // down the street
        Assert.True(DustRules.CloudDensity(Rocket, Origin + new Vector3(0, 1, 0), 10.5) > 0.95f);
    }

    [Fact]
    public void OnlyLargeBlastsSendAWave()
    {
        Assert.False(DustRules.HasFront(6f));
        Assert.False(DustRules.HasFront(8f)); // the gunship's rockets: several a second
        Assert.True(DustRules.HasFront(9f));
        Assert.Null(DustRules.FrontArrival(6f, 10f));
        Assert.Equal(0f, DustRules.FeltDelay(6f, 30f));
        Assert.Equal(0f, DustRules.FrontDensity(Rocket, Origin + new Vector3(20, 1, 0), 10.5));
    }

    [Fact]
    public void TheWaveSlowsToAStopAtItsReach()
    {
        var reach = DustRules.FrontReach(9f);
        var t = DustRules.FrontSeconds(9f);
        Assert.Equal(0f, DustRules.FrontRadius(9f, 0));
        Assert.Equal(reach, DustRules.FrontRadius(9f, t), 3);
        Assert.Equal(reach, DustRules.FrontRadius(9f, t + 5), 3);
        // it starts at the tuned speed and is slower later
        Assert.Equal(DustRules.FrontSpeedMps, DustRules.FrontRadius(9f, 0.001) / 0.001f, 0);
        var early = DustRules.FrontRadius(9f, t * 0.2) - DustRules.FrontRadius(9f, t * 0.1);
        var lateStep = DustRules.FrontRadius(9f, t * 0.9) - DustRules.FrontRadius(9f, t * 0.8);
        Assert.True(early > lateStep * 3);
        // the particles' damping stops them at the same place: v0^2 / 2a = reach
        Assert.Equal(reach, DustRules.FrontSpeedMps * DustRules.FrontSpeedMps / (2 * DustRules.FrontDeceleration(9f)), 3);
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(25f)]
    [InlineData(50f)]
    public void TheWaveArrivesWhenItsRadiusIsTheDistance(float distance)
    {
        var arrival = DustRules.FrontArrival(9f, distance);
        Assert.NotNull(arrival);
        Assert.Equal(distance, DustRules.FrontRadius(9f, arrival!.Value), 2);
        Assert.Equal(arrival.Value, DustRules.FeltDelay(9f, distance), 4);
    }

    [Fact]
    public void BeyondTheReachTheSoundRunsOn()
    {
        var reach = DustRules.FrontReach(9f);
        Assert.Null(DustRules.FrontArrival(9f, reach + 1));
        var justPast = DustRules.FeltDelay(9f, reach + 1);
        Assert.True(justPast >= DustRules.FrontSeconds(9f));
        Assert.True(DustRules.FeltDelay(9f, reach + 341) > justPast + 0.9f);
    }

    [Fact]
    public void TheWaveVeilsTheEyeOnlyOnceItArrivesAndThenThins()
    {
        var eye = Origin + new Vector3(30, 1.6f, 0);
        var arrival = DustRules.FrontArrival(9f, 30f)!.Value;
        Assert.Equal(0f, DustRules.FrontDensity(Vehicle, eye, 10.0 + arrival - 0.05));
        var peak = DustRules.FrontDensity(Vehicle, eye, 10.0 + arrival + DustRules.FrontRiseSeconds);
        Assert.True(peak > 0.5f);
        Assert.True(DustRules.FrontDensity(Vehicle, eye, 10.0 + arrival + 1.5) < peak * 0.5f);
        Assert.True(DustRules.FrontDensity(Vehicle, eye, 10.0 + arrival + 8) < 0.01f);
        // weaker further out, nothing above it
        var far = Origin + new Vector3(50, 1.6f, 0);
        Assert.True(DustRules.FrontDensity(Vehicle, far, 10.0 + DustRules.FrontArrival(9f, 50f)!.Value + DustRules.FrontRiseSeconds) < peak);
        Assert.Equal(0f, DustRules.FrontDensity(Vehicle, eye + new Vector3(0, 20, 0), 10.0 + arrival + 0.2));
    }

    [Fact]
    public void ABurstInOneSpotKeepsOneCloud()
    {
        var field = new DustField();
        Assert.True(field.Add(Rocket));
        Assert.False(field.Add(new DustBlast(Origin + new Vector3(1, 0, 1), 6f, 10.3)));
        Assert.True(field.Add(new DustBlast(Origin + new Vector3(12, 0, 0), 6f, 10.3))); // elsewhere
        Assert.True(field.Add(new DustBlast(Origin, 12f, 10.4))); // a much larger blast is its own
        Assert.True(field.Add(new DustBlast(Origin, 6f, 13.0))); // the first cloud is old by now
        Assert.Equal(4, field.Count);
    }

    [Fact]
    public void TheFieldIsBoundedAndForgetsOldDust()
    {
        var field = new DustField();
        for (var i = 0; i < DustField.Max + 5; i++) field.Add(new DustBlast(Origin + new Vector3(i * 40, 0, 0), 6f, 10.0));
        Assert.Equal(DustField.Max, field.Count);
        field.Prune(10.0 + DustRules.Seconds(6f) + 0.1);
        Assert.Equal(0, field.Count);
    }

    [Fact]
    public void DustOnTheLineHidesWhatIsBehindIt()
    {
        var field = new DustField();
        var a = Origin + new Vector3(-30, 1.6f, 0);
        var b = Origin + new Vector3(30, 1.6f, 0);
        Assert.Equal(0f, field.Sight(a, b, 10.5));
        field.Add(Rocket);
        Assert.True(field.Sight(a, b, 10.5) >= DustRules.SightBlocked);
        Assert.True(field.Sight(a, a + new Vector3(0, 0, 40), 10.5) < DustRules.SightBlocked); // a line that misses the cloud
        Assert.Equal(0f, field.Sight(a, b, 20.0));
    }

    [Fact]
    public void AWallBetweenTheEyeAndTheBlastKeepsMostOfTheDustOut()
    {
        var field = new DustField();
        field.Add(Rocket);
        var eye = Origin + new Vector3(2, 1.6f, 0);
        var open = field.Veil(eye, 10.5);
        var behind = field.Veil(eye, 10.5, _ => 0.3f);
        Assert.True(behind < open * 0.5f);
    }

    [Fact]
    public void ABlastCanNameItsWavesSpeed()
    {
        // half the speed: the wave takes twice as long to get anywhere, and reaches as far
        Assert.Equal(DustRules.FrontSeconds(9f) * 2, DustRules.FrontSeconds(9f, DustRules.FrontSpeedMps / 2), 3);
        Assert.Equal(DustRules.FrontArrival(9f, 30f)!.Value * 2, DustRules.FrontArrival(9f, 30f, DustRules.FrontSpeedMps / 2)!.Value, 3);
        Assert.Equal(DustRules.FrontReach(9f), DustRules.FrontRadius(9f, 100, 20f), 3);
        Assert.Equal(DustRules.FrontSpeedMps, DustRules.SpeedOr(0f));
        Assert.Equal(40f, DustRules.SpeedOr(40f));
        // the veil follows the blast's own speed
        var eye = Origin + new Vector3(30, 1.6f, 0);
        var slow = new DustBlast(Origin, 9f, 10.0, 20f);
        var usual = DustRules.FrontArrival(9f, 30f)!.Value;
        Assert.True(DustRules.FrontDensity(Vehicle, eye, 10.0 + usual + 0.1) > 0.3f);
        Assert.Equal(0f, DustRules.FrontDensity(slow, eye, 10.0 + usual + 0.1));
        Assert.True(DustRules.FrontDensity(slow, eye, 10.0 + DustRules.FrontArrival(9f, 30f, 20f)!.Value + 0.1) > 0.3f);
    }

    [Fact]
    public void TheExplosionEventCarriesAWaveSpeedOnlyWhenOneIsNamed()
    {
        var plain = new ExplosionMsg(0x8001, 3, 2, new Vector3(4, 5, 6), 8f);
        Assert.Equal(21, WeaponCodec.Encode(plain).Length); // a rocket's event is as it was
        Assert.Equal(plain, WeaponCodec.DecodeExplosion(WeaponCodec.Encode(plain)));
        var named = plain with { WaveSpeed = 40f };
        Assert.Equal(named, WeaponCodec.DecodeExplosion(WeaponCodec.Encode(named)));
    }
}

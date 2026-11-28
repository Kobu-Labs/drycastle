using Xunit;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scenery;

namespace Drycastle.ShooterCore.Tests.Scenery;

public class WeatherTests
{
    static StormLook Look() => new() { EveryMinutes = new[] { 1.0, 1.0 }, LastingMinutes = new[] { 0.5, 0.5 }, StrikeSeconds = new[] { 5.0, 5.0 }, StrikeDistanceM = new[] { 150.0, 1500.0 }, FadeSeconds = 10 };

    [Fact]
    public void Clear_for_a_wait_then_a_storm_with_strikes_then_clear_again()
    {
        var w = new WeatherMachine(Look(), new Random(3));
        var changes = new List<WeatherPhase>();
        w.Changed += changes.Add;
        var strikes = new List<LightningStrike>();
        for (var i = 0; i < 59 * 10; i++) Assert.Null(w.Tick(0.1f)); // 59 s: clear, no lightning
        Assert.False(w.Storm);
        for (var i = 0; i < 1000 && changes.Count < 1; i++) w.Tick(0.1f);
        Assert.True(w.Storm);
        for (var i = 0; i < 29 * 10; i++) if (w.Tick(0.1f) is { } s) strikes.Add(s); // 29 s of the 30 s storm: 5 s apart
        Assert.InRange(strikes.Count, 4, 6);
        Assert.All(strikes, s => { Assert.InRange(s.BearingDeg, 0f, 360f); Assert.InRange(s.DistanceM, 150f, 1500f); });
        for (var i = 0; i < 100 && changes.Count < 2; i++) w.Tick(0.1f);
        Assert.Equal(new[] { WeatherPhase.Storm, WeatherPhase.Clear }, changes);
        Assert.False(w.Storm);
    }

    [Fact]
    public void An_admin_can_force_either_phase()
    {
        var w = new WeatherMachine(Look(), new Random(1));
        Assert.True(w.ForceStorm());
        Assert.True(w.Storm);
        Assert.False(w.ForceStorm()); // already a storm: no change, the storm's time starts again
        Assert.True(w.ForceClear());
        Assert.False(w.Storm);
        Assert.Null(w.Tick(10f)); // no lightning in clear weather
    }

    [Fact]
    public void The_events_round_trip_and_refuse_garbage()
    {
        Assert.Equal(72, (int)EventKind.WeatherState);
        Assert.Equal(73, (int)EventKind.Lightning);
        Assert.True(WeatherCodec.TryDecodeState(WeatherCodec.EncodeState(WeatherPhase.Storm), out var phase));
        Assert.Equal(WeatherPhase.Storm, phase);
        Assert.False(WeatherCodec.TryDecodeState(new byte[] { 9 }, out _));
        Assert.True(WeatherCodec.TryDecodeStrike(WeatherCodec.EncodeStrike(new LightningStrike(271.4f, 812f)), out var s));
        Assert.Equal((271.4f, 812f), (s.BearingDeg, s.DistanceM));
        Assert.False(WeatherCodec.TryDecodeStrike(new byte[] { 1, 2, 3 }, out _));
        var back = Assert.IsType<EventMsg>(Wire.DecodeReliable(Wire.Encode(new EventMsg((byte)EventKind.Lightning, WeatherCodec.EncodeStrike(new LightningStrike(0, 150))))));
        Assert.True(WeatherCodec.TryDecodeStrike(back.Payload, out _));
    }

    [Fact]
    public void A_full_storm_dims_the_sun_and_thickens_the_fog_and_thunder_arrives_late_and_quiet()
    {
        var day = DayCurve.Sample(12.0, 0.1f, DayCurve.MoonPeakEnergy, DayCurve.ExposureNight);
        Assert.Equal(day, StormSky.Apply(day, 0f, 0f));
        var storm = StormSky.Apply(day, 1f, 0f);
        Assert.Equal(day.SunEnergy * 0.2f, storm.SunEnergy, 4);
        Assert.Equal(day.AmbientEnergy * 0.6f, storm.AmbientEnergy, 4);
        Assert.Equal(day.FogDensity * 3f, storm.FogDensity, 6);
        Assert.True(StormSky.Apply(day, 1f, 1f).AmbientEnergy > storm.AmbientEnergy);
        Assert.Equal(1f, StormSky.FlashAt(0f), 3);
        Assert.Equal(0f, StormSky.FlashAt(StormSky.FlashSeconds));
        Assert.Equal(0f, StormSky.ThunderDb(150f), 3);
        Assert.Equal(-18f, StormSky.ThunderDb(1500f), 3);
        Assert.Equal(3f, StormSky.ThunderDelaySeconds(1029f), 3);
    }

    [Fact]
    public void Thunder_is_a_crack_when_close_and_a_rumble_when_far_and_the_rain_follows_the_storm_and_the_shelter()
    {
        Assert.Equal("weather.thunder.near", StormSky.ThunderSound(150f));
        Assert.Equal("weather.thunder.far", StormSky.ThunderSound(StormSky.NearThunderM));
        Assert.Equal("weather.thunder.far", StormSky.ThunderSound(1500f));
        Assert.Equal(StormSky.RainSilentDb, StormSky.RainDb(0f, 0f, 0f, -14f));
        Assert.Equal(0f, StormSky.RainDb(1f, 0f, 0f, -14f), 3);
        Assert.Equal(-14f, StormSky.RainDb(1f, 1f, 0f, -14f), 3);
        Assert.True(StormSky.RainDb(0.5f, 0f, 0f, -14f) < StormSky.RainDb(1f, 0f, 0f, -14f));
        Assert.Equal(20000f, StormSky.RainCutoffHz(0f), 1);
        Assert.Equal(700f, StormSky.RainCutoffHz(1f), 1);
    }

    [Fact]
    public void The_shipped_storm_numbers_parse_and_bad_ranges_are_refused()
    {
        var c = EnvironmentConfig.Load(Path.Combine(AppContext.BaseDirectory, "data", "environment.json"));
        Assert.Equal(new[] { 20.0, 40.0 }, c.Storm.EveryMinutes);
        Assert.Equal(20f, c.Storm.FadeSeconds);
        Assert.InRange(c.Storm.RainAmount, 1, 20000);
        Assert.Throws<Drycastle.ShooterCore.Data.DataFileException>(() => EnvironmentConfig.Parse("{\"version\":1,\"storm\":{\"every_minutes\":[40,20]}}"));
        Assert.Throws<Drycastle.ShooterCore.Data.DataFileException>(() => EnvironmentConfig.Parse("{\"version\":1,\"storm\":{\"strike_seconds\":[0,5]}}"));
        Assert.Throws<Drycastle.ShooterCore.Data.DataFileException>(() => EnvironmentConfig.Parse("{\"version\":1,\"storm\":{\"rain_amount\":-1}}"));
        Assert.Throws<Drycastle.ShooterCore.Data.DataFileException>(() => EnvironmentConfig.Parse("{\"version\":1,\"storm\":{\"rain_indoor_db\":6}}"));
    }
}

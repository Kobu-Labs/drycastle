using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

public class DayClockTests
{
    static DayClock Clock(int startMinute, double dayLengthSeconds = 24 * 60, bool frozen = false) =>
        new() { StartMinute = startMinute, DayLengthSeconds = dayLengthSeconds, Frozen = frozen };

    [Theory]
    [InlineData("00:00", 0)]
    [InlineData("06:30", 390)]
    [InlineData("23:59", 1439)]
    public void ParsesHhMm(string s, int minute) => Assert.Equal(minute, EnvironmentConfig.ParseHhMm(s));

    [Theory]
    [InlineData("24:00")]
    [InlineData("6:30")]
    [InlineData("12:60")]
    [InlineData("noon")]
    public void RejectsBadTimes(string s) => Assert.Throws<DataFileException>(() => EnvironmentConfig.ParseHhMm(s));

    [Fact]
    public void StartTimeIsTheHourAtElapsedZero()
    {
        Assert.Equal(6.5, Clock(390).HourAt(0), 6);
    }

    [Fact]
    public void HourAdvancesOneGameHourPerDayLengthOverTwentyFour()
    {
        // 24 real minutes for a day: one game hour is one real minute
        var c = Clock(8 * 60);
        Assert.Equal(9.0, c.HourAt(60), 6);
        Assert.Equal(8.0 + 12.0, c.HourAt(12 * 60), 6);
    }

    [Fact]
    public void HourWrapsPastMidnight()
    {
        // start 23:00, 60 s day: 30 s is half a day, so +12 h -> 11:00
        var c = Clock(23 * 60, dayLengthSeconds: 60);
        Assert.Equal(11.0, c.HourAt(30), 6);
        Assert.Equal(23.0, c.HourAt(60), 6);
    }

    [Fact]
    public void FrozenClockDoesNotMove()
    {
        var c = Clock(12 * 60, frozen: true);
        Assert.Equal(12.0, c.HourAt(0), 6);
        Assert.Equal(12.0, c.HourAt(9999), 6);
    }

    [Fact]
    public void TickMapsToElapsedSecondsAtTheTickRate()
    {
        var c = Clock(0, dayLengthSeconds: 24 * 60);
        // 30 ticks a second: tick 1800 is 60 s, one game hour
        Assert.Equal(c.HourAt(60), c.HourAtTick(1800, Wire.TickRate), 9);
    }

    [Fact]
    public void SunIsHighAtNoonAndBelowAtMidnight()
    {
        Assert.Equal(DayCurve.MaxSunElevationDeg, DayCurve.Sample(12, 0.35f).SunElevationDeg, 3);
        Assert.Equal(-DayCurve.MaxSunElevationDeg, DayCurve.Sample(0, 0.35f).SunElevationDeg, 3);
        Assert.Equal(0f, DayCurve.Sample(6, 0.35f).SunElevationDeg, 3);
        Assert.Equal(0f, DayCurve.Sample(18, 0.35f).SunElevationDeg, 3);
    }

    [Fact]
    public void NightFactorIsZeroByDayAndOneAtNight()
    {
        Assert.Equal(0f, DayCurve.NightFactorAt(12), 6);
        Assert.Equal(0f, DayCurve.NightFactorAt(10), 6);
        Assert.Equal(1f, DayCurve.NightFactorAt(0), 6);
        Assert.Equal(1f, DayCurve.NightFactorAt(3), 6);
    }

    [Fact]
    public void NightFactorRisesThroughDuskAndFallsThroughDawn()
    {
        float prev = -1f;
        for (var h = 15.0; h <= 21.0; h += 0.25)
        {
            var n = DayCurve.NightFactorAt(h);
            Assert.True(n >= prev, $"dusk not monotonic at {h}");
            prev = n;
        }
        prev = 2f;
        for (var h = 4.0; h <= 8.0; h += 0.25)
        {
            var n = DayCurve.NightFactorAt(h);
            Assert.True(n <= prev, $"dawn not monotonic at {h}");
            prev = n;
        }
    }

    [Fact]
    public void NightFactorMatchesLightingSample()
    {
        for (var h = 0.0; h < 24; h += 0.5)
            Assert.Equal(DayCurve.NightFactorAt(h), DayCurve.Sample(h, 0.35f).NightFactor, 6);
    }

    [Fact]
    public void SunEnergyFadesAtNightAndMoonLightsTheNight()
    {
        var midnight = DayCurve.Sample(0, 0.35f);
        var noon = DayCurve.Sample(12, 0.35f);
        Assert.Equal(0f, midnight.SunEnergy, 6);
        Assert.Equal(0f, noon.MoonEnergy, 6);
        Assert.Equal(DayCurve.MoonPeakEnergy, midnight.MoonEnergy, 3);
        Assert.True(noon.SunEnergy > 1f);
    }

    [Fact]
    public void AmbientNeverDropsBelowTheNightFloor()
    {
        const float floor = 0.35f;
        for (var h = 0.0; h < 24; h += 0.25)
            Assert.True(DayCurve.Sample(h, floor).AmbientEnergy >= floor - 1e-6f, $"ambient below floor at {h}");
        Assert.Equal(floor, DayCurve.Sample(0, floor).AmbientEnergy, 6);
        Assert.True(DayCurve.Sample(12, floor).AmbientEnergy > DayCurve.Sample(0, floor).AmbientEnergy);
    }

    [Fact]
    public void NightIsExposedHigherAndStarsShowOnlyAtNight()
    {
        var noon = DayCurve.Sample(12, 0.35f);
        var midnight = DayCurve.Sample(0, 0.35f);
        Assert.Equal(DayCurve.ExposureNight, midnight.Exposure, 6);
        Assert.Equal(DayCurve.ExposureDay, noon.Exposure, 6);
        Assert.Equal(0f, noon.StarVisibility, 6);
        Assert.Equal(1f, midnight.StarVisibility, 6);
    }

    [Fact]
    public void EnvironmentJsonParsesAndBuildsTheClock()
    {
        var c = EnvironmentConfig.Parse("""{ "version": 1, "start_time": "07:15", "day_length_minutes": 30, "min_ambient": 0.2 }""");
        Assert.Equal(0.2f, c.MinAmbient, 6);
        var clock = c.ToClock();
        Assert.Equal(7 * 60 + 15, clock.StartMinute);
        Assert.Equal(30 * 60, clock.DayLengthSeconds, 6);
        Assert.False(clock.Frozen);
    }

    [Fact]
    public void FrozenTimeInFileFreezesAtThatTime()
    {
        var c = EnvironmentConfig.Parse("""{ "version": 1, "start_time": "08:00", "frozen_time": "21:30" }""");
        var clock = c.ToClock();
        Assert.True(clock.Frozen);
        Assert.Equal(21.5, clock.HourAt(500), 6);
    }

    [Fact]
    public void FlagsOverrideTheFile()
    {
        var c = EnvironmentConfig.Parse("""{ "version": 1, "start_time": "08:00", "day_length_minutes": 24 }""");
        var clock = c.ToClock(timeOverride: "02:45", dayLengthMinutesOverride: 10, freeze: true);
        Assert.True(clock.Frozen);
        Assert.Equal(2.75, clock.HourAt(0), 6);
        Assert.Equal(600, clock.DayLengthSeconds, 6);
    }

    [Fact]
    public void BadEnvironmentFilesFailWithTheFieldName()
    {
        var e1 = Assert.Throws<DataFileException>(() => EnvironmentConfig.Parse("""{ "version": 1, "start_time": "25:00" }"""));
        Assert.Contains("start_time", e1.Message);
        Assert.Throws<DataFileException>(() => EnvironmentConfig.Parse("""{ "version": 1, "day_length_minutes": 0 }"""));
        Assert.Throws<DataFileException>(() => EnvironmentConfig.Parse("""{ "version": 1, "min_ambient": 2 }"""));
        Assert.Throws<DataFileException>(() => EnvironmentConfig.Parse("""{ "start_time": "08:00" }"""));
    }

    [Fact]
    public void WelcomeCarriesTheClock()
    {
        var w = new WelcomeMsg(Wire.ProtocolVersion, 7, Wire.TickRate, 123456, StartMinute: 1234, DayLengthMs: 1440000, Frozen: true);
        Assert.Equal(w, Wire.DecodeReliable(Wire.Encode(w)));
    }
}

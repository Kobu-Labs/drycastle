using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The reversing beep of the bigger vehicles (#432).</summary>
public class ReverseBeepTests
{
    const float Dt = 1f / 60f;
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));

    static int Beeps(ref ReverseBeep b, float speed, float seconds, bool active = true)
    {
        var n = 0;
        for (var t = 0f; t < seconds; t += Dt) if (b.Step(speed, Dt, active)) n++;
        return n;
    }

    [Fact]
    public void It_beeps_at_once_when_the_vehicle_backs_up_then_every_cycle()
    {
        var b = new ReverseBeep();
        Assert.True(b.Step(-3, Dt, true));
        Assert.Equal(2, Beeps(ref b, -3, ReverseBeep.Cycle * 2 + 0.05f)); // at 1.4 s and 2.8 s
    }

    [Fact]
    public void Driving_forwards_or_creeping_back_is_silent()
    {
        var b = new ReverseBeep();
        Assert.Equal(0, Beeps(ref b, 10, 5));
        Assert.Equal(0, Beeps(ref b, -0.5f, 5));
        Assert.Equal(0, Beeps(ref b, -5, 5, active: false)); // nobody drives it, or out of earshot
    }

    [Fact]
    public void Rocking_back_and_forth_cannot_chatter()
    {
        var b = new ReverseBeep();
        var n = 0;
        for (var t = 0f; t < 2f; t += Dt) if (b.Step(((int)(t * 4)) % 2 == 0 ? -4 : 4, Dt, true)) n++; // 4 reversals a second
        Assert.Equal(2, n); // the first, and one a cycle later
    }

    [Fact]
    public void Exactly_the_bigger_vehicles_have_the_alarm()
    {
        var ids = Shipped.Vehicles.Where(v => v.ReverseBeep).Select(v => v.Id).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "amphibious", "bus", "flak", "harvester", "humvee", "scud", "truck" }, ids);
        Assert.All(Shipped.Vehicles.Where(v => v.ReverseBeep), v => Assert.True(v.Mass >= 2000));
        Assert.All(Shipped.Vehicles.Where(v => !v.ReverseBeep && v.Medium != VehicleMedium.Air && v.Trailer == null), v => Assert.True(v.Mass < 2000, v.Id));
    }
}

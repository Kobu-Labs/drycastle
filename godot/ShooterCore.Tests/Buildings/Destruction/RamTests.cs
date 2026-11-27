using System.Numerics;
using Drycastle.ShooterCore.Buildings.Destruction;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

/// <summary>HouseRam: the momentum rule the thin fences use, and the vehicle box's frames. A hollow house is ploughed (PloughTests).</summary>
public class RamTests
{
    [Fact]
    public void Decide_goes_through_on_momentum_over_the_cost()
    {
        // 20 000 kg m/s of wall: a car (1200 kg) losing 13.9 m/s stops (the whole impact is its crash); at 20 m/s into it, head on, it goes through at 3.33 m/s
        var stop = HouseRam.Decide(20000, 1200, 13.9f, 13.9f);
        Assert.False(stop.Through);
        Assert.Equal(13.9f, stop.SpeedLost);
        var through = HouseRam.Decide(20000, 1200, 20f, 20f);
        Assert.True(through.Through);
        Assert.Equal(20f - 20000f / 1200, through.SpeedKept, 3);
        Assert.Equal(20000f / 1200, through.SpeedLost, 3);
        // a glancing hit: the wall took less than the speed; momentum is mass times what it took, the kept speed is off the whole speed
        var glance = HouseRam.Decide(20000, 6500, 5f, 25f);
        Assert.True(glance.Through);
        Assert.Equal(25f - 20000f / 6500, glance.SpeedKept, 3);
        Assert.False(HouseRam.Decide(20000, 1200, 16.6f, 16.6f).Through); // just under: 19 920
    }

    [Fact]
    public void Box_from_godot_faces_its_yaw()
    {
        var b = RamBox.FromGodot(new Vector3(1, 2, 3), 0, 1, 2, 1.6f);
        Assert.Equal(new Vector3(1, -3, 2.8f), b.Centre);
        Assert.Equal(0, Vector3.Distance(Vector3.UnitY, b.Fwd), 5); // yaw 0 faces -z in Godot: north
        Assert.Equal(new Vector3(5, 6, -7), RamBox.ToGodot(RamBox.ToMap(new Vector3(5, 6, -7))));
    }
}

using Drycastle.ShooterCore.Scenery;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Scenery;

public class MeteorTests
{
    [Fact]
    public void SameTimeSameSky()
    {
        for (var t = 0.0; t < 400; t += 0.37)
            Assert.Equal(Meteors.Active(t), Meteors.Active(t));
    }

    [Fact]
    public void OccasionalNotConstant()
    {
        var n = 0; var total = 0;
        for (var t = 0.0; t < 3000; t += 0.25) { total++; if (Meteors.Active(t).Count > 0) n++; }
        var share = n / (double)total;
        Assert.InRange(share, 0.02, 0.25);
    }

    [Fact]
    public void ValuesAreSane()
    {
        for (var t = 0.0; t < 3000; t += 0.25)
        {
            var list = Meteors.Active(t);
            Assert.True(list.Count <= Meteors.MaxActive);
            foreach (var m in list)
            {
                Assert.InRange(m.Progress, 0f, 1f);
                Assert.InRange(m.Brightness, 0f, 2.6f);
                Assert.InRange(m.From.Length(), 0.99f, 1.01f);
                Assert.True(m.From.Y > 0f && m.To.Y > 0f, "stays above the horizon");
            }
            Assert.InRange(Meteors.Glow(list), 0f, 3f);
        }
    }

    [Fact]
    public void FireballComesOnce()
    {
        var seen = 0; var was = false;
        for (var t = 0.0; t < 300; t += 0.1)
        {
            var big = Meteors.Active(t).Any(m => m.Brightness > 1.2f);
            if (big && !was) seen++;
            was = big;
        }
        Assert.Equal(1, seen);
    }

    [Fact]
    public void OnlyInTheDark()
    {
        Assert.Equal(0f, Meteors.NightGate(0f));
        Assert.Equal(0f, Meteors.NightGate(0.8f));
        Assert.Equal(1f, Meteors.NightGate(1f));
    }
}

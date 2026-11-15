using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class LifetimeTests
{
    [Fact]
    public void ExpiresOnTheTickTheAgeReachesTheLimitAndStaysExpired()
    {
        var l = new Lifetime();
        Assert.False(l.Tick(0.5f, 1f));
        Assert.True(l.Tick(0.5f, 1f)); // >=, not >
        Assert.True(l.Tick(0.1f, 1f));
    }

    [Fact]
    public void IsBitIdenticalToTheHandWrittenSum()
    {
        float age = 0;
        var l = new Lifetime();
        for (var i = 0; i < 5000; i++)
        {
            var dt = 0.016f + (i % 7) * 0.0013f;
            age += dt;
            Assert.Equal(age >= 30.7f, l.Tick(dt, 30.7f));
            Assert.Equal(BitConverter.SingleToInt32Bits(age), BitConverter.SingleToInt32Bits(l.Age));
        }
    }

    [Fact]
    public void ResetStartsOver()
    {
        var l = new Lifetime();
        l.Tick(2f, 1f);
        l.Reset();
        Assert.Equal(0f, l.Age);
        Assert.False(l.Tick(0.5f, 1f));
    }
}

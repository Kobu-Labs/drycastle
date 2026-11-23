using Xunit;
using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Tests.Fsm;

public class LazyInitTests
{
    [Fact]
    public void BeginIsTrueOnceAndAFailedTryIsNotRepeated()
    {
        var l = new LazyInit<string>();
        Assert.Equal(LazyState.Untried, l.State);
        Assert.True(l.Begin());
        Assert.Equal(LazyState.Failed, l.State);
        Assert.False(l.Begin());
        Assert.Null(l.Value);
        Assert.True(l.Tried);
    }

    [Fact]
    public void ReadyHoldsTheValueAndStopsFurtherTries()
    {
        var l = new LazyInit<string>();
        Assert.True(l.Begin());
        l.Ready("x");
        Assert.Equal(LazyState.Ready, l.State);
        Assert.Equal("x", l.Value);
        Assert.False(l.Begin());
    }

    [Fact]
    public void AReentrantCallDuringTheTryDoesNotTryAgain()
    {
        var l = new LazyInit<string>();
        var runs = 0;
        string? Ensure(ref LazyInit<string> self)
        {
            if (!self.Begin()) return self.Value;
            runs++;
            var inner = Ensure(ref self); // the init asks for itself
            Assert.Null(inner);
            self.Ready("done");
            return self.Value;
        }
        Assert.Equal("done", Ensure(ref l));
        Assert.Equal(1, runs);
    }

    [Fact]
    public void ResetAllowsANewTry()
    {
        var l = new LazyInit<string>();
        l.Begin(); l.Ready("a");
        l.Reset();
        Assert.Null(l.Value);
        Assert.True(l.Begin());
    }
}

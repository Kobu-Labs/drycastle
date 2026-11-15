using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class ConnectionPhasesTests
{
    [Fact]
    public void ASessionJoinsThroughLoadingOrDirectly()
    {
        var slow = new StateMachine<SessionPhase>(ConnectionPhases.Session);
        Assert.Equal(SessionPhase.Connected, slow.Current);
        { // Strict is on for every test (StateMachineTests.Init): an undeclared transition throws
            slow.Enter(SessionPhase.Loading);
            slow.Enter(SessionPhase.Joined);
            var guest = new StateMachine<SessionPhase>(ConnectionPhases.Session);
            guest.Enter(SessionPhase.Joined);
            var bot = new StateMachine<SessionPhase>(ConnectionPhases.Session);
            bot.Enter(SessionPhase.Joined);
        }
    }

    [Fact]
    public void ARejectedSessionNeverJoinsAndAJoinedOneNeverGoesBack()
    {
        var spec = ConnectionPhases.Session;
        Assert.False(spec.Allows(SessionPhase.Rejected, SessionPhase.Loading));
        Assert.False(spec.Allows(SessionPhase.Rejected, SessionPhase.Joined));
        Assert.False(spec.Allows(SessionPhase.Joined, SessionPhase.Loading));
        Assert.False(spec.Allows(SessionPhase.Loading, SessionPhase.Rejected));
        Assert.Contains("Rejected", spec.Terminal);
    }

    [Fact]
    public void TheLinkRunsConnectingWelcomedSpawnedAndBackOnRespawn()
    {
        var l = new StateMachine<LinkPhase>(ConnectionPhases.Link);
        { // Strict is on for every test (StateMachineTests.Init): an undeclared transition throws
            l.Tick(3);
            Assert.Equal(3f, l.Elapsed);
            l.Enter(LinkPhase.Welcomed);
            l.Enter(LinkPhase.Spawned);
            l.Enter(LinkPhase.Welcomed); // respawn: waiting for the spawn position
            l.Enter(LinkPhase.Spawned);
            l.Enter(LinkPhase.Failed);
            l.Enter(LinkPhase.Left);
        }
        Assert.True(l.Current.IsEnded());
    }

    [Fact]
    public void TheEndsAreFinalExceptFailedThenLeft()
    {
        var spec = ConnectionPhases.Link;
        Assert.False(spec.Allows(LinkPhase.Left, LinkPhase.Welcomed));
        Assert.False(spec.Allows(LinkPhase.Failed, LinkPhase.Spawned));
        Assert.False(spec.Allows(LinkPhase.Connecting, LinkPhase.Spawned));
        Assert.True(spec.Allows(LinkPhase.Failed, LinkPhase.Left));
        Assert.False(LinkPhase.Welcomed.IsEnded());
    }
}

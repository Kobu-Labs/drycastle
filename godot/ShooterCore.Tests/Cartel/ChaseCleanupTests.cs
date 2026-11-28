using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Fsm;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>When a chase car and its crew leave the world (<see cref="ChaseCleanup"/>; docs/cartel.md "Chase cars"): gave up, destroyed and its wreck lain its time, the Heist ended, a player aboard, a half-seated spawn. Strict.</summary>
public class ChaseCleanupTests
{
    const float Dt = 1f / 30f;

    public ChaseCleanupTests() => StateMachineDiagnostics.Strict = true;

    static readonly ChaseLeft Whole = new(true, false, false, true, 3);

    [Fact]
    public void ADestroyedCarLiesItsWreckTimeThenTheCarIsDueAndTheCrewOnceItsBodiesAreGone()
    {
        var d = new ChaseData();
        var machine = new ChaseCar(d);
        var cleanup = new ChaseCleanup(d);
        machine.Step(new ChaseFacts(50, 10, 10, true, false, true), Dt); // Spawned -> Chasing
        machine.Step(new ChaseFacts(50, 10, 10, true, true, true), Dt);            // the car blew up: Gone
        cleanup.Exploded();
        Assert.Equal(ChaseState.Gone, machine.State);
        var lain = 0f;
        while (lain < d.WreckSeconds - 0.5f)
        {
            Assert.False(cleanup.WreckOver(true, Dt), $"wreck gone after {lain:F1} s");
            Assert.Equal(new ChaseDue(false, false), ChaseCleanup.Due(machine.State, new ChaseLeft(true, true, false, true, 2))); // the wreck stays, the crew's bodies lie
            lain += Dt;
        }
        var over = false;
        for (var i = 0; i < 60 && !over; i++) over = cleanup.WreckOver(true, Dt);
        Assert.True(over); // the wreck has lain wreck_seconds: the car goes
        Assert.False(cleanup.WreckOver(true, Dt)); // once
        Assert.Equal(new ChaseDue(false, true), ChaseCleanup.Due(machine.State, new ChaseLeft(false, false, false, true, 0))); // the car is out, the crew is nothing now
        Assert.Equal(new ChaseDue(false, false), ChaseCleanup.Due(machine.State, new ChaseLeft(false, false, false, true, 1))); // a member still lives or lies
    }

    [Fact]
    public void APlayerAboardKeepsTheCarUntilTheyLeave()
    {
        Assert.Equal(new ChaseDue(false, false), ChaseCleanup.Due(ChaseState.Gone, new ChaseLeft(true, false, true, true, 1)));
        Assert.Equal(new ChaseDue(true, false), ChaseCleanup.Due(ChaseState.Gone, new ChaseLeft(true, false, false, true, 1)));
    }

    [Fact]
    public void ACarThatIsStillChasingOrWhoseCrewIsOutIsLeftAlone()
    {
        foreach (var s in new[] { ChaseState.Spawned, ChaseState.Chasing, ChaseState.Stopping })
            Assert.Equal(default, ChaseCleanup.Due(s, new ChaseLeft(true, false, false, true, 0)));
        Assert.Equal(default, ChaseCleanup.Due(ChaseState.Out, new ChaseLeft(true, false, false, true, 0))); // the crew is out: Gone first
    }

    [Fact]
    public void GivingUpTakesTheCarAndTheCrewAtOnceAPlayerAboardOrNot()
    {
        Assert.Equal(new ChaseDue(true, true), ChaseCleanup.Due(ChaseState.GivingUp, Whole));
        Assert.Equal(new ChaseDue(true, true), ChaseCleanup.Due(ChaseState.GivingUp, new ChaseLeft(true, false, true, true, 3)));
    }

    [Fact]
    public void TheHeistEndingOrAHalfSeatedSpawnTakesWhatIsLeftAndOnlyThat()
    {
        foreach (var s in new[] { ChaseState.Spawned, ChaseState.Chasing, ChaseState.Out })
            Assert.Equal(new ChaseDue(true, true), ChaseCleanup.Due(s, Whole, ended: true));
        Assert.Equal(new ChaseDue(false, true), ChaseCleanup.Due(ChaseState.Out, new ChaseLeft(false, false, false, true, 2), ended: true)); // the car was already taken
        Assert.Equal(default, ChaseCleanup.Due(ChaseState.Gone, new ChaseLeft(false, false, false, false, 0), ended: true));
    }
}

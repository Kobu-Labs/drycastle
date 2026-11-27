using System.Numerics;
using Drycastle.ShooterCore.Thieves;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Thieves;

/// <summary>The progress check of a Gang member (<see cref="StuckWatch"/>, thieves.json gang.stuck): reroute at stuck_seconds, snap at snap_seconds, never while moving or not trying.</summary>
public class StuckWatchTests
{
    const float Dt = 0.1f;
    static readonly GangStuck Cfg = new() { StuckM = 1, StuckSeconds = 3, SnapSeconds = 6 };

    /// <summary>Steps for <paramref name="seconds"/> with the member at <paramref name="at"/>; the actions it gave, in order, with their time.</summary>
    static List<(float At, StuckAction Do)> Run(StuckWatch w, float from, float seconds, Func<float, Vector3> at, bool trying = true)
    {
        var did = new List<(float, StuckAction)>();
        for (var t = from; t < from + seconds - 1e-4f; t += Dt)
            if (w.Step(Dt, at(t), trying) is var a && a != StuckAction.None) did.Add((t, a));
        return did;
    }

    [Fact]
    public void AMemberStandingStillIsRerouted_AtStuckSeconds_AndSnapped_AtSnapSeconds()
    {
        var w = new StuckWatch(Cfg);
        var did = Run(w, 0, 6.5f, _ => Vector3.Zero);
        Assert.Equal(new[] { StuckAction.Reroute, StuckAction.Snap }, did.Select(d => d.Do));
        Assert.InRange(did[0].At, 2.9f, 3.3f);
        Assert.InRange(did[1].At, 5.9f, 6.4f);
    }

    [Fact]
    public void PacingInPlaceIsNoProgress()
    {
        var w = new StuckWatch(Cfg);
        var did = Run(w, 0, 3.5f, t => new Vector3(MathF.Sin(t * 8) * 0.4f, 0, 0)); // back and forth within 0.8 m
        Assert.Equal(StuckAction.Reroute, Assert.Single(did).Do);
    }

    [Fact]
    public void AMemberThatMakesAMetreEveryFewSecondsIsNotStuck()
    {
        var w = new StuckWatch(Cfg);
        Assert.Empty(Run(w, 0, 30, t => new Vector3(t * 0.5f, 0, 0))); // half a metre a second
        Assert.Empty(Run(w, 30, 30, t => new Vector3(15 + (t - 30) * 0.4f, 0, 0))); // a metre every 2.5 s: under stuck_seconds
        Assert.Equal(StuckPhase.Watching, w.Phase);
    }

    [Fact]
    public void ProgressAfterTheRerouteGoesBackToWatchingAndSnapsNothing()
    {
        var w = new StuckWatch(Cfg);
        var did = Run(w, 0, 3.5f, _ => Vector3.Zero);
        Assert.Equal(StuckAction.Reroute, Assert.Single(did).Do);
        Assert.Equal(StuckPhase.Rerouted, w.Phase);
        Assert.Empty(Run(w, 3.5f, 10, t => new Vector3(2 + (t - 3.5f), 0, 0))); // it got on
        Assert.Equal(StuckPhase.Watching, w.Phase);
    }

    [Fact]
    public void NotTryingResetsTheCheckAndASnappedMemberIsWatchedAgainFromWhereItStands()
    {
        var w = new StuckWatch(Cfg);
        Run(w, 0, 2.5f, _ => Vector3.Zero);
        Assert.Empty(Run(w, 2.5f, 5, _ => Vector3.Zero, trying: false)); // it stood to fight: no clock
        Assert.Equal(StuckPhase.Off, w.Phase);
        Assert.Empty(Run(w, 7.5f, 2.5f, _ => Vector3.Zero)); // a fresh clock
        var did = Run(w, 10, 3.5f, _ => Vector3.Zero);
        Assert.Equal(StuckAction.Reroute, Assert.Single(did).Do);
    }

    [Fact]
    public void ASnapRestartsTheCheckSoAMemberStillStuckIsRerouted_ThenSnappedAgain()
    {
        var w = new StuckWatch(Cfg);
        var did = Run(w, 0, 13, _ => Vector3.Zero);
        Assert.Equal(new[] { StuckAction.Reroute, StuckAction.Snap, StuckAction.Reroute, StuckAction.Snap }, did.Select(d => d.Do));
    }
}

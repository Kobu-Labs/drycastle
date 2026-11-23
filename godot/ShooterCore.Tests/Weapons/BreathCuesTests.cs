using Xunit;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Tests.Weapons;

public class BreathCuesTests
{
    const BreathState.BreathPhase Ready = BreathState.BreathPhase.Ready, Holding = BreathState.BreathPhase.Holding, Exhaled = BreathState.BreathPhase.Exhaled;

    [Fact]
    public void A_hold_begins_with_an_inhale_and_an_early_release_is_a_soft_exhale()
    {
        var c = new BreathCues();
        Assert.Equal(default, c.Tick(0, Ready, 1f));
        Assert.True(c.Tick(0.1, Holding, 0.98f).Inhale);
        Assert.False(c.Tick(0.2, Holding, 0.95f).Inhale);
        var release = c.Tick(0.3, Ready, 0.95f);
        Assert.True(release.Exhale);
        Assert.False(release.ForcedExhale);
    }

    [Fact]
    public void No_heartbeat_while_enough_breath_is_left()
    {
        var c = new BreathCues();
        for (var i = 0; i < 100; i++) Assert.False(c.Tick(i * 0.033, Holding, 1f - i * 0.005f).Beat); // down to 0.5
    }

    [Fact]
    public void The_heartbeat_starts_at_once_when_the_breath_is_low_and_gets_faster_and_louder()
    {
        var c = new BreathCues();
        c.Tick(0, Holding, 0.5f);
        var beats = new List<(double At, float Db)>();
        // a 4 s budget: the last 40 % is 1.6 s
        for (var t = 0.0; t <= 1.6; t += 1 / 60.0)
        {
            var cue = c.Tick(10 + t, Holding, (float)(BreathCues.HeartbeatFrom * (1 - t / 1.6)));
            if (cue.Beat) beats.Add((t, cue.BeatDb));
        }
        Assert.True(beats.Count >= 3);
        Assert.True(beats[0].At <= 1 / 30.0); // the first frame under the threshold
        Assert.Equal(BreathCues.QuietBeatDb, beats[0].Db, 0);
        for (var i = 2; i < beats.Count; i++)
        {
            Assert.True(beats[i].At - beats[i - 1].At < beats[i - 1].At - beats[i - 2].At + 0.02);
            Assert.True(beats[i].Db > beats[i - 1].Db);
        }
    }

    [Fact]
    public void A_forced_exhale_is_loud_once_and_ends_the_heartbeat()
    {
        var c = new BreathCues();
        c.Tick(0, Holding, 0.05f);
        var forced = c.Tick(0.1, Exhaled, 0f);
        Assert.True(forced.ForcedExhale);
        Assert.False(forced.Exhale);
        Assert.False(forced.Beat);
        Assert.Equal(default, c.Tick(0.2, Exhaled, 0f));
        Assert.Equal(default, c.Tick(1.2, Ready, 0f));
    }
}

using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Jobs;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>The "Job done" cinematic (docs/cartel.md "Job done"): the timeline's stage at a time, the flash, the music's level, the skip, damage, reduce motion, and cartel.json job_done_cinematic.</summary>
public class JobDoneCinematicTests
{
    static readonly JobDoneCinematicData Shipped = CartelFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "cartel.json")).JobDoneCinematic;

    static CartelJobMsg End(int id) => new(id, JobState.Succeeded, JobFailReason.None, "harvest", 2, 1, 0, 100, CartelJobFlags.Earns);

    static JobDone Started(bool camera = true, int id = 1)
    {
        var d = new JobDone(Shipped);
        Assert.True(d.OnJob(End(id), camera));
        return d;
    }

    // ---- the timeline ----

    [Fact]
    public void TheShippedFileGivesFourLegsOfAboutTenSecondsOutOnly()
    {
        var t = JobDoneTimeline.Full(Shipped);
        Assert.Equal(4, t.LegCount); // out in three moves, then the cut back
        Assert.Equal(9.6f, t.Seconds, 0.01f);
        Assert.Equal(4f, t.BannerSeconds);
        Assert.Equal(12f, t.MusicEndSeconds, 0.001f); // the music outlasts the camera by two seconds
    }

    [Fact]
    public void TheStageAtATimeIsTheLegThatIsPlayingMovingThenHolding()
    {
        var t = JobDoneTimeline.Full(Shipped);
        var behind = Shipped.Stages.FindIndex(s => s.Id == "behind");
        var above = Shipped.Stages.FindIndex(s => s.Id == "above");
        var high = Shipped.Stages.FindIndex(s => s.Id == "high");

        var f = t.At(0f); // the first move: from the player's own eye to behind
        Assert.Equal((0, -1, behind, true, 0f), (f.Leg, f.From, f.To, f.Moving, f.Move));

        f = t.At(0.35f); // arrived: holding
        Assert.False(f.Moving);
        Assert.Equal(1f, f.Move);
        Assert.Equal(0f, f.Hold, 3);
        Assert.Equal(0.5f, t.At(0.35f + 0.75f).Hold, 3); // half of the 1.5 s hold
        Assert.Equal(behind, t.At(1.8f).To);

        f = t.At(1.85f + 0.1f); // the second move: behind to above
        Assert.Equal((1, behind, above, true), (f.Leg, f.From, f.To, f.Moving));

        f = t.At(4.7f + 0.35f + 1f); // the third leg's hold: the high view
        Assert.Equal((2, above, high, false), (f.Leg, f.From, f.To, f.Moving));

        f = t.At(9.55f + 0.02f); // no way back down: one quick cut from the high view into the eye
        Assert.Equal((3, high, -1, true), (f.Leg, f.From, f.To, f.Moving));

        f = t.At(60f); // past the end: arrived at the eye
        Assert.Equal((3, -1, false, 1f), (f.Leg, f.To, f.Moving, f.Move));
    }

    [Fact]
    public void AMoveIsFastAtFirstAndSettlesAndNeverGoesBackwards()
    {
        Assert.Equal(0f, JobDoneTimeline.Ease(0f));
        Assert.Equal(1f, JobDoneTimeline.Ease(1f));
        Assert.True(JobDoneTimeline.Ease(0.25f) > 0.6f); // most of the way in the first quarter
        var prev = 0f;
        for (var u = 0f; u <= 1f; u += 0.02f) { var e = JobDoneTimeline.Ease(u); Assert.True(e >= prev); prev = e; }
    }

    [Fact]
    public void EachMoveStartsASoftFlashThatIsGoneBeforeTheHoldAndNeverPassesThePeak()
    {
        var t = JobDoneTimeline.Full(Shipped);
        var peak = 0f;
        for (var s = 0f; s < t.Seconds; s += 0.005f) { var a = t.FlashAt(s); Assert.InRange(a, 0f, Shipped.FlashPeak + 1e-4f); peak = Math.Max(peak, a); }
        Assert.Equal(Shipped.FlashPeak, peak, 0.02f);
        foreach (var start in t.FlashStarts)
        {
            Assert.Equal(0f, t.FlashAt(start + Shipped.FlashSeconds + 0.001f)); // over after flash_seconds
            Assert.True(t.FlashAt(start + Shipped.FlashRiseSeconds) > Shipped.FlashPeak * 0.95f);
        }
        Assert.Equal(0f, t.FlashAt(1.2f)); // in a hold
    }

    [Fact]
    public void NoMoreThanThreeFlashesStartInAnySecondAndTooTightLegsAreRefused()
    {
        var starts = JobDoneTimeline.Full(Shipped).FlashStarts.ToArray();
        foreach (var from in starts)
            Assert.True(starts.Count(s => s >= from && s < from + 1f) <= JobDoneCinematicData.MaxFlashesPerSecond);

        // legs of 0.2 s each would flash five times a second
        Assert.Throws<DataFileException>(() => CartelFile.Parse("{\"version\":1,\"job_done_cinematic\":{\"legs\":[{\"to\":\"behind\",\"move_seconds\":0.1,\"hold_seconds\":0.1},{\"to\":\"eye\",\"move_seconds\":0.35,\"hold_seconds\":0}],\"stages\":[{\"id\":\"behind\",\"up_m\":3}]}}"));
    }

    [Fact]
    public void ABadCinematicIsRefusedByName()
    {
        static void Refused(string json, string field) =>
            Assert.Contains(field, Assert.Throws<DataFileException>(() => CartelFile.Parse("{\"version\":1,\"job_done_cinematic\":" + json + "}")).Message);
        Refused("{\"legs\":[{\"to\":\"nowhere\",\"move_seconds\":0.3,\"hold_seconds\":1}]}", "unknown stage");
        Refused("{\"legs\":[{\"to\":\"behind\",\"move_seconds\":0.3,\"hold_seconds\":1}]}", "last leg");
        Refused("{\"flash_peak\":0.9}", "flash_peak");
        Refused("{\"flash_seconds\":0.5}", "flash_seconds");
        Refused("{\"stages\":[{\"id\":\"behind\",\"up_m\":3,\"anchor\":\"moon\"}]}", "anchor");
        Refused("{\"stages\":[{\"id\":\"behind\",\"up_m\":3},{\"id\":\"behind\",\"up_m\":5}]}", "used twice");
    }

    [Fact]
    public void TheMusicComesUpHoldsAndFadesOutOverItsLastThreeSecondsEndingTwoSecondsAfterTheCamera()
    {
        var t = JobDoneTimeline.Full(Shipped);
        Assert.Equal(0f, t.MusicGain(0f));
        Assert.Equal(1f, t.MusicGain(Shipped.MusicInSeconds));
        Assert.Equal(1f, t.MusicGain(8.99f));
        Assert.Equal(0f, t.MusicGain(12f));
        Assert.Equal(0f, t.MusicGain(30f));
        var prev = 1f;
        for (var s = 9f; s <= 12f; s += 0.05f) { var g = t.MusicGain(s); Assert.True(g <= prev + 1e-6f); prev = g; }
        Assert.True(t.MusicGain(10f) is > 0.1f and < 0.8f); // still audible when the camera is back
    }

    [Fact]
    public void ThePartyCentreIsTheMeanOfThePlayerAndTheLivingMatesNearThem()
    {
        var me = new Vector3(10, 0, 10);
        Assert.Equal(me, CinematicRules.PartyCentre(me, Array.Empty<Vector3>(), 30f));
        Assert.Equal(new Vector3(15, 0, 10), CinematicRules.PartyCentre(me, new[] { new Vector3(20, 0, 10) }, 30f));
        Assert.Equal(new Vector3(15, 0, 10), CinematicRules.PartyCentre(me, new[] { new Vector3(20, 0, 10), new Vector3(300, 0, 10) }, 30f)); // one is too far
    }

    [Fact]
    public void AStageStandsBehindTheHeadingAndAboveAndDriftsInAndRoundDuringItsHold()
    {
        var behind = Shipped.Stages.First(s => s.Id == "behind");
        var (eye, look) = behind.Offsets(0f, 0f); // yaw 0 faces -Z: behind is +Z
        Assert.Equal(new Vector3(0, 3, 6), eye);
        Assert.Equal(new Vector3(0, 1.2f, 0), look);
        var (late, _) = behind.Offsets(0f, 1f);
        Assert.True(new Vector2(late.X, late.Z).Length() < 6f - 0.7f); // pushed in
        Assert.True(late.X != 0f); // and turned
        Assert.Equal(3f, late.Y);
        var (turnedView, _) = behind.Offsets(MathF.PI / 2, 0f); // facing -X (turned left): behind is +X
        Assert.Equal(6f, turnedView.X, 0.001f);
        Assert.Equal(0f, turnedView.Z, 0.001f);
    }

    // ---- the cinematic as the client lives it ----

    [Fact]
    public void ThePlayerIsLockedOutOfTheControlsWhileTheCameraIsOutAndBackAtTheEnd()
    {
        var d = Started();
        Assert.True(d.Camera);
        Assert.True(d.InputLocked);
        Assert.NotNull(d.Frame);
        d.Tick(5f);
        Assert.True(d.InputLocked);
        d.Tick(5.1f);
        Assert.False(d.Camera);
        Assert.False(d.InputLocked);
        Assert.Null(d.Frame);
        Assert.Equal(0f, d.Flash);
    }

    [Fact]
    public void ASkipEndsTheCameraAtOnceFlashesBackFadesTheMusicOverOneAndAHalfSecondsAndGivesTheControlsBackAfterAMoment()
    {
        var d = Started();
        d.Tick(6f); // in the high view's hold
        var musicBefore = d.MusicGain;
        Assert.Equal(1f, musicBefore);
        Assert.True(d.Interrupt());
        Assert.False(d.Camera);
        Assert.False(d.Playing);
        Assert.False(d.Showing);
        Assert.Null(d.Frame);
        Assert.True(d.InputLocked);                 // the key that skipped does nothing else
        Assert.Equal(musicBefore, d.MusicGain, 3);  // the music is where it was and fades from there
        d.Tick(0.05f);
        Assert.True(d.Flash > 0.4f);                // the cut back is covered by a flash
        d.Tick(0.3f);
        Assert.False(d.InputLocked);
        Assert.Equal(0f, d.Flash);
        d.Tick(0.6f);
        Assert.InRange(d.MusicGain, 0.05f, 0.7f);
        d.Tick(0.6f);                               // 1.55 s after the skip
        Assert.False(d.MusicOn);
        Assert.Equal(0f, d.MusicGain);
        Assert.False(d.Interrupt());                // nothing left to end
    }

    [Fact]
    public void ASkipRightAfterALegsFlashWaitsForAThirdOfASecondBeforeItsOwn()
    {
        var d = Started();
        d.Tick(1.85f + 0.1f); // 0.1 s into the second move's flash
        Assert.True(d.Flash > 0f);
        d.Interrupt();
        d.Tick(0.1f);                                     // 0.2 s after that flash started: the leg's flash is over, ours has not begun
        Assert.Equal(0f, d.Flash);
        d.Tick(0.16f);                                    // 0.36 s after it started: ours may
        Assert.True(d.Flash > 0f);
    }

    [Fact]
    public void DamageTakenEndsTheCameraLikeASkipButAFirstSightOrHealingDoesNot()
    {
        var d = Started();
        d.ObserveHealth(100f); // the first sight is only the level
        d.Tick(1f);
        d.ObserveHealth(100f);
        Assert.True(d.Camera);
        d.ObserveHealth(100f);
        d.Tick(0.5f);
        d.ObserveHealth(99f);
        Assert.False(d.Camera);

        var healed = Started(id: 2);
        healed.ObserveHealth(60f);
        healed.ObserveHealth(70f);
        Assert.True(healed.Camera);
        healed.ObserveHealth(69.9f);
        Assert.False(healed.Camera);
    }

    [Fact]
    public void WithReduceMotionOnlyTheBannerAndTheMusicRunWithNoCameraNoFlashAndNoLock()
    {
        var d = Started(camera: false);
        Assert.True(d.Playing);
        Assert.False(d.Camera);
        Assert.False(d.InputLocked);
        Assert.Null(d.Frame);
        Assert.True(d.Showing);
        Assert.False(d.Interrupt()); // no camera to end
        for (var s = 0f; s < 4f; s += 0.05f)
        {
            Assert.Equal(0f, d.Flash);
            Assert.False(d.InputLocked);
            d.Tick(0.05f);
        }
        Assert.False(d.Playing);
        Assert.False(d.Showing);
        Assert.False(d.MusicOn); // the banner's 4 s are the music's too
        d.ObserveHealth(100f); d.ObserveHealth(1f);
        Assert.False(d.InputLocked);
    }

    [Fact]
    public void ReducedMusicFadesOverItsOwnLastSeconds()
    {
        var t = JobDoneTimeline.Reduce(Shipped);
        Assert.False(t.HasCamera);
        Assert.Equal(4f, t.Seconds);
        Assert.Equal(1f, t.MusicGain(2f));
        Assert.True(t.MusicGain(3.2f) < 0.5f);
        Assert.Equal(0f, t.MusicGain(4f));
        Assert.Equal(0f, t.FlashAt(0.01f));
    }

    [Fact]
    public void ANewJobStartsItOverAndAHealthDropBeforeItDoesNotEndIt()
    {
        var d = Started();
        d.ObserveHealth(100f);
        d.Tick(3f);
        Assert.True(d.OnJob(End(2)));
        Assert.Equal(2, d.Starts);
        Assert.Equal(0f, d.Elapsed);
        Assert.True(d.Camera);
        d.ObserveHealth(100f); // a level is not a drop
        Assert.True(d.Camera);
        d.Interrupt();
        Assert.True(d.OnJob(End(3))); // after a skip too
        Assert.True(d.Camera);
    }

    [Fact]
    public void TheFrameNamesEachLegInTurnAndAClearEndsEverything()
    {
        var d = Started();
        var seen = new List<int>();
        for (var i = 0; i < 1100; i++) { if (d.Frame is { } f && (seen.Count == 0 || seen[^1] != f.Leg)) seen.Add(f.Leg); d.Tick(0.01f); }
        Assert.Equal(new[] { 0, 1, 2, 3 }, seen);
        var e = Started(id: 9);
        e.Clear();
        Assert.False(e.Playing);
        Assert.False(e.MusicOn);
        Assert.False(e.InputLocked);
    }
}

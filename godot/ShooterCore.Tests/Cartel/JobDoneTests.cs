using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Jobs;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

public class JobDoneTests
{
    static CartelJobMsg End(int id, JobState state = JobState.Succeeded, int paid = 0, CartelJobFlags flags = CartelJobFlags.Earns)
        => new(id, state, JobFailReason.None, "harvest", 2, 1, 0, paid, flags);

    [Fact]
    public void ASucceededJobShowsTheBannerWithTheRespectPaidForFourSecondsWhileTheCinematicRunsAboutTenAndTheMusicTwelve()
    {
        var b = new JobDone();
        Assert.False(b.Showing);
        Assert.True(b.OnJob(End(7, paid: 240)));
        Assert.True(b.Showing);
        Assert.Equal(new JobDoneDetail(240, null), b.Detail);
        Assert.True(b.Detail.Earned);
        b.Tick(3.9f);
        Assert.True(b.Showing);
        b.Tick(0.2f);
        Assert.False(b.Showing);   // the banner is gone ...
        Assert.True(b.Playing);    // ... the cinematic is not
        b.Tick(5.4f);              // 9.5 s
        Assert.True(b.Playing);
        b.Tick(0.2f);              // 9.7 s: the camera is back, the music still fades
        Assert.False(b.Playing);
        Assert.False(b.Camera);
        Assert.True(b.MusicOn);
        Assert.True(b.MusicGain > 0f);
        b.Tick(2.5f);              // past 12 s (the step that ended the camera spent its rest of 0.1 s)
        Assert.False(b.MusicOn);
        Assert.Equal(0f, b.MusicGain);
    }

    [Fact]
    public void AFailedRunningOrEmptyJobShowsNothingAndOneJobIsAnnouncedOnce()
    {
        var b = new JobDone();
        Assert.False(b.OnJob(End(1, JobState.Failed)));
        Assert.False(b.OnJob(End(1, JobState.Active)));
        Assert.False(b.OnJob(End(0)));
        Assert.False(b.Showing);
        Assert.True(b.OnJob(End(2, paid: 10)));
        b.Tick(5f);
        Assert.False(b.OnJob(End(2, paid: 10))); // the same end again (a flag changed): not again
        Assert.False(b.Showing);
        Assert.True(b.OnJob(End(3, paid: 10)));
        Assert.True(b.Showing);
    }

    [Fact]
    public void ANoRespectPlayerIsToldWhyAndAMemberPaidNothingSeesNoSecondLine()
    {
        Assert.Equal(new JobDoneDetail(0, NoRespectReason.Guest), JobDone.DetailOf(End(1, flags: CartelJobFlags.Guest)));
        Assert.Equal(new JobDoneDetail(0, NoRespectReason.NotMember), JobDone.DetailOf(End(1, flags: CartelJobFlags.NotMember)));
        Assert.Equal(new JobDoneDetail(0, NoRespectReason.Sandbox), JobDone.DetailOf(End(1, flags: CartelJobFlags.None)));
        var nothing = JobDone.DetailOf(End(1, paid: 0, flags: CartelJobFlags.Earns));
        Assert.True(nothing.Hidden);
        Assert.False(nothing.Earned);
        Assert.Equal(new JobDoneDetail(55, null), JobDone.DetailOf(End(1, paid: 55, flags: CartelJobFlags.Earns | CartelJobFlags.Leader)));
    }

    [Fact]
    public void ClearingForgetsTheBannerAndALaterConnectionAnnouncesTheSameJobIdAgain()
    {
        var b = new JobDone();
        b.OnJob(End(4, paid: 5));
        b.Clear();
        Assert.False(b.Showing);
        Assert.True(b.OnJob(End(4, paid: 5)));
    }
}

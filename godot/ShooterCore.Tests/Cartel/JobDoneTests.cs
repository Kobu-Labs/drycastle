using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Jobs;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

public class JobDoneTests
{
    static CartelJobMsg End(int id, JobState state = JobState.Succeeded, int paid = 0, CartelJobFlags flags = CartelJobFlags.Earns)
        => new(id, state, JobFailReason.None, "harvest", 2, 1, 0, paid, flags);

    [Fact]
    public void ASucceededJobShowsTheBannerWithTheRespectPaidForFourSecondsThenItIsGone()
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
        Assert.False(b.Showing);
        Assert.Equal(0f, b.Pull);
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
    public void TheCameraEasesOutHoldsAndEasesBackOverTheBannersFourSeconds()
    {
        Assert.Equal(0f, JobDone.PullAt(0f));
        Assert.Equal(0.5f, JobDone.PullAt(JobDone.EaseSeconds / 2), 0.001f);
        Assert.Equal(1f, JobDone.PullAt(JobDone.EaseSeconds), 0.001f);
        Assert.Equal(1f, JobDone.PullAt(JobDone.Seconds / 2), 0.001f);
        Assert.Equal(0.5f, JobDone.PullAt(JobDone.Seconds - JobDone.EaseSeconds / 2), 0.001f);
        Assert.Equal(0f, JobDone.PullAt(JobDone.Seconds));
        var prev = 0f;
        for (var t = 0f; t <= JobDone.EaseSeconds; t += 0.05f) { var p = JobDone.PullAt(t); Assert.True(p >= prev); prev = p; }
        var b = new JobDone();
        b.OnJob(End(1, paid: 5));
        b.Tick(2f);
        Assert.Equal(1f, b.Pull, 0.001f);
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

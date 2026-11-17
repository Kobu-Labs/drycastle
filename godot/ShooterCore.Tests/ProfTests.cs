using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class ProfTests
{
    [Fact]
    public void Every_scope_has_a_name()
    {
        for (var s = (Prof.S)0; s < Prof.S.Count; s++) Assert.False(string.IsNullOrEmpty(Prof.Name(s)));
        Assert.Equal("log_players", Prof.Name(Prof.S.Count - 1));
    }

    [Fact]
    public void A_scope_that_ran_is_in_the_period_and_gone_after_the_reset()
    {
        Prof.Reset();
        Prof.TickBegin();
        var t = Prof.Begin();
        t = Prof.Lap(Prof.S.Pets, t);
        Prof.End(Prof.S.Pets, t);
        Assert.Contains("pets=", Prof.Scopes(1));
        Assert.Contains("|2|", Prof.Scopes(1)); // two calls in one second, then the kilobytes
        Assert.True(Prof.TickEnd(5));
        Assert.False(Prof.TickEnd(4)); // not slower than the slowest
        Prof.Reset();
        Assert.DoesNotContain("pets=", Prof.Scopes(1));
        Assert.True(Prof.TickEnd(1)); // a new period starts from nothing
        Prof.Reset();
    }

    [Fact]
    public void A_thread_line_gives_its_name_and_cpu_time()
    {
        // tid (comm) state ppid pgrp session tty tpgid flags minflt cminflt majflt cmajflt utime stime ...
        const string line = "4242 (WorkerThread 12) S 1 4242 4242 0 -1 4194304 10 0 0 0 250 50 0 0 20 0 30 0 100 1000 200";
        Assert.True(ProcStat.Parse(line, out var comm, out var ticks));
        Assert.Equal("WorkerThread 12", comm);
        Assert.Equal(300, ticks);
        Assert.True(ProcStat.Parse("7 (odd) name)) R 1 7 7 0 -1 0 0 0 0 0 3 4 0 0", out comm, out ticks));
        Assert.Equal("odd) name)", comm);
        Assert.Equal(7, ticks);
        Assert.False(ProcStat.Parse("garbage", out _, out _));
    }

    [Theory]
    [InlineData("WorkerThread 12", "workerthread")]
    [InlineData("bot-nav", "bot_nav")]
    [InlineData(".NET Finalizer", "net_finalizer")]
    [InlineData("42", "thread")]
    public void Threads_of_one_kind_share_a_key(string comm, string key) => Assert.Equal(key, ProcStat.Group(comm));
}

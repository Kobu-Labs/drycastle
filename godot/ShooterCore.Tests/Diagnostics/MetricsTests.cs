using Xunit;
using Drycastle.ShooterCore.Diagnostics;

namespace Drycastle.ShooterCore.Tests.Diagnostics;

public class MetricsTests
{
    [Fact]
    public void ALineHasItsMeasurementTagAndFields()
    {
        var b = new MetricsBatch();
        b.Begin("drycastle_tick").Field("p50_ms", 10.5).Field("overruns", 0).End();
        b.Begin("drycastle_phase", "phase", "match").Field("p99_us", 3977).End();
        Assert.Equal("drycastle_tick p50_ms=10.5,overruns=0\ndrycastle_phase,phase=match p99_us=3977\n", b.Take());
        Assert.True(b.Empty);
    }

    [Fact]
    public void ALineWithoutAFiniteFieldIsLeftOut()
    {
        var b = new MetricsBatch();
        b.Begin("a").Field("x", double.NaN).Field("y", double.PositiveInfinity).End();
        b.Begin("b").Field("x", double.NaN).Field("y", 1).End();
        Assert.Equal("b y=1\n", b.Take());
    }

    [Fact]
    public void TheMemoryAndGcLinesBecomeFields()
    {
        var b = new MetricsBatch();
        b.Fields("drycastle_gc", "last_index=358 gen=1 pause_ms=3.6 gen0_kb=2111>187 note=none compacted=1");
        Assert.Equal("drycastle_gc last_index=358,gen=1,pause_ms=3.6,gen0_kb_before=2111,gen0_kb_after=187,compacted=1\n", b.Take());
    }

    [Fact]
    public void ThreadsAndScopesBecomeALineEach()
    {
        var b = new MetricsBatch();
        b.Tagged("drycastle_thread", "thread", "main=35.1 bot_nav=17.7", "cpu_pct");
        b.Tagged("drycastle_scope", "scope", "animal_step=41.60|2533|65 path=3.5|12.1", "ms_s", "calls_s", "kb_s");
        Assert.Equal(
            "drycastle_thread,thread=main cpu_pct=35.1\ndrycastle_thread,thread=bot_nav cpu_pct=17.7\n" +
            "drycastle_scope,scope=animal_step ms_s=41.6,calls_s=2533,kb_s=65\ndrycastle_scope,scope=path ms_s=3.5,calls_s=12.1\n", b.Take());
    }

    [Fact]
    public void NamesKeepToLettersDigitsAndUnderscores()
    {
        var b = new MetricsBatch();
        b.Begin("dry castle", "th,read", "Worker Thread=3").Field("cpu pct", 1).End();
        Assert.Equal("dry_castle,th_read=Worker_Thread_3 cpu_pct=1\n", b.Take());
    }

    [Fact]
    public async Task ASendThatFailsIsSaidOnceAndDoesNotThrow()
    {
        var said = new List<string>();
        using var sender = new MetricsSender("http://127.0.0.1:9/write", s => { lock (said) said.Add(s); });
        for (var i = 0; i < 3; i++)
        {
            sender.Send("a x=1\n");
            for (var w = 0; w < 100 && Interlocked.Read(ref sender.Failed) + Interlocked.Read(ref sender.Dropped) <= i; w++) await Task.Delay(50);
        }
        Assert.Equal(3, sender.Failed + sender.Dropped);
        Assert.True(sender.Failed >= 1);
        lock (said) Assert.Single(said);
    }
}

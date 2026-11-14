using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The Thieves event's plumbing (docs/events/thieves/01): the per-kind handler table behind /event, thieves.json and the phase of the event.</summary>
public class ThievesTests
{
    // ---- the handler table: /event <kind>, /event current end|cancel ----

    /// <summary>The three kinds as the server registers them (InitEvents); each handler books its own end in the book, as the real ones do.</summary>
    static (EventHandlers<string> Handlers, EventBook Book, List<string> Log) Table()
    {
        var book = new EventBook();
        var log = new List<string>();
        var h = new EventHandlers<string>();
        foreach (var (kind, canCancel) in new[] { (EventBook.Bigfoot, true), (EventBook.Mass, false), (EventBook.Thieves, true) })
        {
            h.Register(kind, kind, who => { log.Add($"start {kind} {who}"); book.Start(kind, 1); },
                who => { log.Add($"end {kind} {who}"); book.End(kind, EventPhase.Won, 2); },
                canCancel ? who => { log.Add($"cancel {kind} {who}"); book.End(kind, EventPhase.Despawned, 2); } : null);
        }
        return (h, book, log);
    }

    [Theory]
    [InlineData(EventBook.Bigfoot)]
    [InlineData(EventBook.Mass)]
    [InlineData(EventBook.Thieves)]
    public void EndGoesToTheHandlerOfTheRunningKindOnly(string kind)
    {
        var (h, book, log) = Table();
        Assert.True(h.Start(kind, "ann"));
        Assert.Equal(EventHandled.Done, h.End(book, "bob"));
        Assert.Equal(new[] { $"start {kind} ann", $"end {kind} bob" }, log); // no other kind's handler ran
        Assert.Equal(EventPhase.Won, book.Last!.Phase);
        Assert.Null(book.Current);
    }

    [Fact]
    public void CancelRunsTheHandlerOrSaysTheKindCanOnlyEnd()
    {
        var (h, book, log) = Table();
        h.Start(EventBook.Thieves, "ann");
        Assert.Equal(EventHandled.Done, h.Cancel(book, "bob", out _));
        Assert.Equal(EventPhase.Despawned, book.Last!.Phase);
        h.Start(EventBook.Mass, "ann");
        Assert.Equal(EventHandled.CannotCancel, h.Cancel(book, "bob", out var name));
        Assert.Equal(EventBook.Mass, name);
        Assert.NotNull(book.Current); // refused: it still runs
        Assert.DoesNotContain(log, l => l.StartsWith("cancel mass"));
    }

    [Fact]
    public void NothingRunningMeansNoHandlerRuns()
    {
        var (h, book, log) = Table();
        Assert.Equal(EventHandled.NoneRunning, h.End(book, "bob"));
        Assert.Equal(EventHandled.NoneRunning, h.Cancel(book, "bob", out _));
        Assert.Empty(log);
        Assert.False(h.Start("fireworks", "ann"));
        Assert.Throws<ArgumentException>(() => h.Register("fireworks", "x", _ => { }, _ => { })); // only the book's kinds
        Assert.Throws<ArgumentException>(() => h.Register(EventBook.Mass, "again", _ => { }, _ => { }));
    }

    [Fact]
    public void ASecondStartIsRefusedWithTheGenericTextNamingTheRunningKind()
    {
        var book = new EventBook();
        Assert.True(EventBook.IsStartable(EventBook.Thieves));
        Assert.NotNull(book.Start(EventBook.Thieves, 1));
        foreach (var kind in new[] { EventBook.Bigfoot, EventBook.Mass, EventBook.Thieves })
        {
            var why = book.WhyNotStart(kind)!;
            Assert.Contains("thieves event is already running", why);
            Assert.Contains("/event current end", why);
        }
        Assert.Null(book.Start(EventBook.Mass, 2));
    }

    [Fact]
    public void TheParserKnowsTheThievesEvent() =>
        Assert.Equal(new[] { "thieves" }, ChatCommands.Parse("/event THIEVES", new ChatContext(true, new[] { "car" })).Args);

    // ---- thieves.json ----

    [Fact]
    public void ThievesJsonNeedsAVersionAndTheShippedFileLoads()
    {
        Assert.Equal(1, ThievesFile.Parse("""{"version":1}""").Version);
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("{}"));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":2}"""));
        var dir = Path.Combine(AppContext.BaseDirectory, "data");
        Assert.Equal(1, GameData.Load(dir).Thieves.Version);
    }

    // ---- the phase ----

    [Fact]
    public void ThePhaseGoesIdleRaidingOverAndTheLastVanGoneWinsTheRun()
    {
        var cfg = new RaidsConfig { Count = 1, MaxVans = 1, NextRaidSeconds = 10, GapSeconds = 1 };
        var t = new ThievesEvent();
        Assert.Equal(ThievesPhase.Idle, t.Phase);
        Assert.Equal(ThievesStep.None, t.Tick(1, 0)); // idle: nothing runs
        Assert.False(t.Finish());
        Assert.True(t.Begin(cfg));
        Assert.False(t.Begin(cfg)); // already raiding
        Assert.Equal(ThievesPhase.Raiding, t.Phase);
        Assert.Equal(ThievesStep.Launch, t.Tick(1, 0));
        Assert.Equal(ThievesStep.None, t.Tick(1, 1)); // the van is in the village
        Assert.Equal(ThievesStep.Won, t.Tick(1, 0)); // and gone: the caller finishes the run
        Assert.True(t.Finish());
        Assert.Equal(ThievesPhase.Over, t.Phase);
        Assert.Equal(ThievesStep.None, t.Tick(1, 0));
        Assert.False(t.Finish());
    }

    [Fact]
    public void ARoundResetClearsAnyPhaseAndAnOverEventCanStartAgain()
    {
        var cfg = new RaidsConfig();
        var t = new ThievesEvent();
        t.Begin(cfg);
        Assert.True(t.Reset()); // mid-run: the round ended it
        Assert.Equal(ThievesPhase.Idle, t.Phase);
        Assert.False(t.Reset());
        t.Begin(cfg);
        t.Finish();
        Assert.True(t.Begin(cfg)); // Over -> Raiding: a new /event thieves
        Assert.Equal(0, t.Launched); // from the first Raid again
        t.Finish();
        Assert.True(t.Reset());
        Assert.Equal(ThievesPhase.Idle, t.Phase);
    }

    [Fact]
    public void TheIntelEntryIsVillageWideAndUnderWayAtOnce()
    {
        var e = ThievesEvent.Intel(50);
        Assert.Equal(ThievesEvent.IntelId, e.Id);
        Assert.Null(e.Where);
        Assert.Null(e.EndsAt); // stays until the run ends and withdraws it
        Assert.Equal(IntelStatus.Underway, IntelTime.Status(e, 50));
        Assert.False(e.Test);
    }
}

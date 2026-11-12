using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class ChatTests
{
    static readonly ChatContext Admin = new(true, new[] { "car", "jeep", "utility_helicopter" });
    static readonly ChatContext Guest = new(false, new[] { "car" });

    // ---- parsing ----

    [Fact]
    public void BotsTakesACountInRange()
    {
        var p = ChatCommands.Parse("/bots 12", Admin);
        Assert.True(p.Ok);
        Assert.Equal("bots", p.Command!.Name);
        Assert.Equal(new[] { "12" }, p.Args);
    }

    [Theory]
    [InlineData("/bots 65")]
    [InlineData("/bots -1")]
    [InlineData("/bots many")]
    [InlineData("/bots 1 2")]
    public void BotsRejectsABadCount(string line) => Assert.NotNull(ChatCommands.Parse(line, Admin).Error);

    [Fact]
    public void BotsWithoutACountAsksForTheCurrentOne() => Assert.True(ChatCommands.Parse("/bots", Admin).Ok);

    [Fact]
    public void SpawnNeedsAKnownVehicleAndNormalisesTheCase()
    {
        Assert.False(ChatCommands.Parse("/spawn", Admin).Ok);
        Assert.False(ChatCommands.Parse("/spawn tank", Admin).Ok);
        Assert.Equal(new[] { "utility_helicopter" }, ChatCommands.Parse("/SPAWN Utility_Helicopter", Admin).Args);
    }

    [Fact]
    public void OnlyAnAdminRunsCommandsButAnyoneCanAskForHelp()
    {
        Assert.Contains("admin", ChatCommands.Parse("/bots 3", Guest).Error);
        Assert.True(ChatCommands.Parse("/help", Guest).Ok);
        Assert.True(ChatCommands.Parse("/help help", Guest).Ok);
    }

    [Fact]
    public void UnknownCommandsAreNamed()
    {
        Assert.Contains("/fly", ChatCommands.Parse("/fly", Admin).Error);
        Assert.NotNull(ChatCommands.Parse("/", Admin).Error);
    }

    [Theory]
    [InlineData("noon", 720, null)]
    [InlineData("midnight", 0, null)]
    [InlineData("14:30", 870, null)]
    [InlineData("9", 540, null)]
    [InlineData("0:05", 5, null)]
    [InlineData("freeze", null, true)]
    [InlineData("resume", null, false)]
    public void TimeAcceptsTimesWordsAndSwitches(string text, int? minute, bool? frozen)
    {
        var t = ChatTime.Parse(text);
        Assert.NotNull(t);
        Assert.Equal(minute, t!.Value.Minute);
        Assert.Equal(frozen, t.Value.Frozen);
        Assert.True(ChatCommands.Parse("/time " + text, Admin).Ok);
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("12:5")]
    [InlineData("1:2:3")]
    [InlineData("evening")]
    [InlineData("")]
    public void TimeRejectsNonsense(string text) => Assert.Null(ChatTime.Parse(text));

    // ---- hints and completion ----

    [Fact]
    public void ASlashListsTheCommandsAndAPrefixNarrowsThem()
    {
        var all = ChatCommands.Hint("/", Admin);
        Assert.Equal(ChatCommands.All.Count, all.Suggestions.Count);
        var b = ChatCommands.Hint("/bo", Admin);
        Assert.Equal("bots", Assert.Single(b.Suggestions).Insert);
        Assert.Equal("/bots [count]", b.Usage);
    }

    [Fact]
    public void AGuestOnlySeesHelp()
    {
        Assert.Equal("help", Assert.Single(ChatCommands.Hint("/", Guest).Suggestions).Insert);
        Assert.NotNull(ChatCommands.Hint("/bots", Guest).Error);
    }

    [Fact]
    public void PlainTextHasNoHint() => Assert.True(ChatCommands.Hint("hello there", Admin).IsEmpty);

    [Fact]
    public void ArgumentsCompleteFromTheirChoices()
    {
        var h = ChatCommands.Hint("/spawn j", Admin);
        Assert.Equal("jeep", Assert.Single(h.Suggestions).Insert);
        Assert.Equal("/spawn jeep", ChatCommands.Complete("/spawn j", h, h.Suggestions[0]));
        var all = ChatCommands.Hint("/spawn ", Admin);
        Assert.Equal(3, all.Suggestions.Count);
        var times = ChatCommands.Hint("/time ", Admin).Suggestions.Select(s => s.Insert).ToList();
        Assert.Contains("noon", times); Assert.Contains("freeze", times);
    }

    [Fact]
    public void ACommandNameCompletesWithASpaceWhenItTakesArguments()
    {
        var h = ChatCommands.Hint("/ti", Admin);
        Assert.Equal("/time ", ChatCommands.Complete("/ti", h, h.Suggestions[0]));
    }

    [Fact]
    public void ThePendingCandidateIsTheOneTabTakesNext()
    {
        var c = new ChatCompleter();
        Assert.Equal(0, c.Pending("/spawn "));
        var t = c.Tab("/spawn ", Admin)!;
        Assert.Equal(1, c.Pending(t));
        Assert.Equal(0, c.Pending("/spawn x"));
    }

    [Fact]
    public void TheHintFlagsWhatCannotBecomeValid()
    {
        Assert.NotNull(ChatCommands.Hint("/bots 99", Admin).Error);
        Assert.Null(ChatCommands.Hint("/bots 6", Admin).Error);
        Assert.NotNull(ChatCommands.Hint("/spawn zzz", Admin).Error);
        Assert.Null(ChatCommands.Hint("/spawn je", Admin).Error);
        Assert.NotNull(ChatCommands.Hint("/bots 3 4", Admin).Error);
        Assert.NotNull(ChatCommands.Hint("/nope", Admin).Error);
    }

    [Fact]
    public void TabCyclesThroughTheCandidatesAndBack()
    {
        var c = new ChatCompleter();
        var first = c.Tab("/spawn ", Admin);
        Assert.Equal("/spawn car", first);
        Assert.Equal("/spawn jeep", c.Tab(first!, Admin));
        Assert.Equal("/spawn utility_helicopter", c.Tab("/spawn jeep", Admin));
        Assert.Equal("/spawn car", c.Tab("/spawn utility_helicopter", Admin)); // wraps
        Assert.Equal("/spawn utility_helicopter", c.Tab("/spawn car", Admin, backwards: true));
    }

    [Fact]
    public void TypingStartsTheCompletionOver()
    {
        var c = new ChatCompleter();
        c.Tab("/spawn ", Admin);
        Assert.Equal("/spawn jeep", c.Tab("/spawn j", Admin)); // not the cycle's next: a fresh word
        Assert.Null(c.Tab("hello", Admin));
    }

    // ---- history ----

    [Fact]
    public void UpWalksBackAndDownReturnsToTheDraft()
    {
        var h = new ChatHistory();
        Assert.Null(h.Older("x"));
        h.Add("one"); h.Add("two"); h.Add("three");
        Assert.Equal("three", h.Older("draft"));
        Assert.Equal("two", h.Older("three"));
        Assert.Equal("one", h.Older("two"));
        Assert.Equal("one", h.Older("one")); // the oldest stays
        Assert.Equal("two", h.Newer());
        Assert.Equal("three", h.Newer());
        Assert.Equal("draft", h.Newer());
        Assert.Null(h.Newer());
    }

    [Fact]
    public void HistorySkipsRepeatsBlanksAndIsBounded()
    {
        var h = new ChatHistory();
        h.Add("a"); h.Add("a"); h.Add("  "); h.Add("");
        Assert.Equal(1, h.Count);
        for (var i = 0; i < ChatHistory.Max + 10; i++) h.Add("line " + i);
        Assert.Equal(ChatHistory.Max, h.Count);
        Assert.Equal("line " + (ChatHistory.Max + 9), h.Older(""));
    }

    // ---- rate limit ----

    [Fact]
    public void TheRateLimiterAllowsABurstThenAboutOneASecond()
    {
        var r = new ChatRateLimiter(3, 1);
        Assert.True(r.TryTake(0)); Assert.True(r.TryTake(0)); Assert.True(r.TryTake(0));
        Assert.False(r.TryTake(0.1));
        Assert.True(r.TryTake(1.2));
        Assert.False(r.TryTake(1.3));
    }

    // ---- wire ----

    [Fact]
    public void ALineSurvivesTheWire()
    {
        var l = new ChatLine(ChatLineKind.Player, "Aničk", "Ahoj světe ✓");
        Assert.Equal(l, ChatEvents.DecodeLine(ChatEvents.EncodeLine(l)));
        Assert.Null(ChatEvents.DecodeLine(new byte[] { 0, 5, 1 }));
        Assert.Null(ChatEvents.DecodeLine(new byte[] { 99, 0, 0, 0 }));
    }

    [Fact]
    public void TextIsCleanedAndCut()
    {
        Assert.Equal("a b", ChatEvents.Clean("  a\u0007 b\n "));
        Assert.Equal(ChatEvents.MaxChars, ChatEvents.Clean(new string('x', 500)).Length);
        Assert.Null(ChatEvents.DecodeSend("\n\t "u8));
        Assert.Equal("/bots 3", ChatEvents.DecodeSend(ChatEvents.EncodeSend("/bots 3")));
    }

    [Fact]
    public void StatusAndClockSurviveTheWire()
    {
        var st = new ChatStatus(true, new[] { "car", "bus" });
        var back = ChatEvents.DecodeStatus(ChatEvents.EncodeStatus(st))!;
        Assert.True(back.Admin);
        Assert.Equal(st.Vehicles, back.Vehicles);
        var ch = new ClockChange(new DayClock { StartMinute = 1234, DayLengthSeconds = 1440, Frozen = true }, 99999);
        var c2 = ChatEvents.DecodeClock(ChatEvents.EncodeClock(ch))!;
        Assert.Equal(1234, c2.Clock.StartMinute);
        Assert.Equal(1440, c2.Clock.DayLengthSeconds, 3);
        Assert.True(c2.Clock.Frozen);
        Assert.Equal(99999u, c2.Tick);
    }

    // ---- the live clock ----

    [Theory]
    [InlineData(0, 12.0)]
    [InlineData(1000.4, 12.0)]
    [InlineData(86400, 3.5)]
    public void SettingTheHourShowsItNow(double elapsed, double hour)
    {
        var clock = new DayClock { StartMinute = 480, DayLengthSeconds = 24 * 60 };
        var set = clock.WithHour(elapsed, hour);
        Assert.InRange(set.HourAt(elapsed), hour - 0.02, hour + 0.02); // within a game minute
        Assert.InRange(set.HourAt(elapsed + 15), hour + 0.23, hour + 0.27); // and the day runs on from there (a day of 24 minutes: 1 s is a game minute)
    }

    [Fact]
    public void FreezingKeepsTheHourAndResumingKeepsItToo()
    {
        var clock = new DayClock { StartMinute = 480, DayLengthSeconds = 24 * 60 };
        var shown = clock.HourAt(500);
        var frozen = clock.WithFrozen(500, true);
        Assert.True(frozen.Frozen);
        Assert.InRange(frozen.HourAt(500), shown - 0.02, shown + 0.02);
        Assert.InRange(frozen.HourAt(5000), shown - 0.02, shown + 0.02);
        var again = frozen.WithFrozen(5000, false);
        Assert.InRange(again.HourAt(5000), shown - 0.02, shown + 0.02);
        Assert.InRange(again.HourAt(5060), shown + 0.98, shown + 1.02);
        Assert.Same(clock, clock.WithFrozen(0, false));
    }

    [Fact]
    public void SettingTheHourOfAFrozenClockStaysFrozenThere()
    {
        var frozen = new DayClock { StartMinute = 480, Frozen = true };
        var set = frozen.WithHour(1234, 21.5);
        Assert.True(set.Frozen);
        Assert.InRange(set.HourAt(99999), 21.48, 21.52);
    }
}

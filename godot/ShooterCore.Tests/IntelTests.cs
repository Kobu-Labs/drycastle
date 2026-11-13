using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The announcement and intel screen's data: the board, its wire payload, the countdown, the test event and the screen's place.</summary>
public class IntelTests
{
    static IntelEvent Event(string id = "bigfoot", double startsAt = 100, Vector2? where = null, bool test = false) =>
        new(id, "creature", "Bigfoot sighting", "Something large was seen in the forest.", new[] { "Thrown trees", "Shockwaves" }, startsAt, where, test);

    // ---- the payload ----

    [Fact]
    public void ABoardSurvivesTheWireOnAnotherClock()
    {
        var sent = new[] { Event("a", 130, new Vector2(12.5f, -340f)), Event("b", 40, null, test: true) };
        var back = IntelCodec.Decode(IntelCodec.Encode(sent, now: 100, announcedId: "a"), now: 5000)!;
        Assert.Equal("a", back.AnnouncedId);
        Assert.Equal("a", back.Announced!.Id);
        Assert.Equal(2, back.Events.Count);
        Assert.Equal(5030, back.Events[0].StartsAt, 2); // the time left (30 s) is what travels
        Assert.Equal(4940, back.Events[1].StartsAt, 2); // an event under way arrives as one that began 60 s ago
        Assert.Equal(new Vector2(12.5f, -340f), back.Events[0].Where);
        Assert.Null(back.Events[1].Where);
        Assert.True(back.Events[1].Test);
        Assert.Equal(sent[0].Risks, back.Events[0].Risks);
        Assert.Equal("Something large was seen in the forest.", back.Events[0].Description);
    }

    [Fact]
    public void AnEmptyBoardAndAnUnannouncedRefreshAreValid()
    {
        var back = IntelCodec.Decode(IntelCodec.Encode(Array.Empty<IntelEvent>(), 0), 0)!;
        Assert.Empty(back.Events);
        Assert.Null(back.Announced);
        Assert.Null(IntelCodec.Decode(IntelCodec.Encode(new[] { Event() }, 0, "nope"), 0)!.Announced); // an id that is not on the board announces nothing
    }

    [Fact]
    public void TextIsCleanedAndCutSoItAlwaysFitsTheScreen()
    {
        var loud = new IntelEvent("x", "k", new string('N', 90) + "\n", new string('d', 900), Enumerable.Range(0, 9).Select(i => new string('r', 99)).ToList(), 0);
        var e = IntelCodec.Decode(IntelCodec.Encode(new[] { loud }, 0), 0)!.Events[0];
        Assert.Equal(IntelCodec.MaxName, e.Name.Length);
        Assert.Equal(IntelCodec.MaxDescription, e.Description.Length);
        Assert.Equal(IntelCodec.MaxRisks, e.Risks.Count);
        Assert.All(e.Risks, r => Assert.Equal(IntelCodec.MaxRisk, r.Length));
        Assert.Equal("ab", IntelCodec.Clean("a\u0007\tb\n", 10));
        Assert.DoesNotContain('\ud83d', IntelCodec.Clean("ab" + "😀", 3)); // a pair is never split
    }

    [Fact]
    public void ACutOrCorruptPayloadIsRefusedAtEveryLength()
    {
        var full = IntelCodec.Encode(new[] { Event("a", 130, new Vector2(1, 2)), Event("b") }, 0, "a");
        for (var n = 0; n < full.Length; n++) Assert.Null(IntelCodec.Decode(full.AsSpan(0, n), 0));
        Assert.NotNull(IntelCodec.Decode(full, 0));
        var wrong = (byte[])full.Clone();
        wrong[0] = 9; // another format
        Assert.Null(IntelCodec.Decode(wrong, 0));
    }

    // ---- the board ----

    [Fact]
    public void TheBoardKeepsTheSoonestFirstAndAnIdReplacesItself()
    {
        var board = new IntelBoard();
        board.Put(Event("late", 300));
        board.Put(Event("soon", 50));
        board.Put(Event("late", 20)); // the same id: replaced, and now the soonest
        Assert.Equal(new[] { "late", "soon" }, board.Events.Select(e => e.Id));
        Assert.Equal(20, board.Events[0].StartsAt);
    }

    [Fact]
    public void TheBoardIsCappedAndDropsTheFurthestInTheFuture()
    {
        var board = new IntelBoard();
        for (var i = 0; i < IntelBoard.MaxEvents + 2; i++) board.Put(Event($"e{i}", i * 10));
        Assert.Equal(IntelBoard.MaxEvents, board.Events.Count);
        Assert.Equal("e0", board.Events[0].Id);
        Assert.DoesNotContain(board.Events, e => e.Id == $"e{IntelBoard.MaxEvents + 1}");
    }

    [Fact]
    public void EventsLeaveTheBoardWhenRemovedOrWhenTheirTimeIsUp()
    {
        var board = new IntelBoard();
        board.Put(Event("a", 10) with { EndsAt = 60 });
        board.Put(Event("b", 10));
        var v = board.Version;
        Assert.False(board.Prune(59));
        Assert.Equal(v, board.Version); // nothing changed: a view would not redraw
        Assert.True(board.Prune(60));
        Assert.Equal(new[] { "b" }, board.Events.Select(e => e.Id));
        Assert.False(board.Remove("a"));
        Assert.True(board.Remove("b"));
        Assert.True(board.IsEmpty);
        Assert.False(board.Clear());
    }

    // ---- time ----

    [Fact]
    public void TheCountdownReadsLikeAClockAndNeverGoesNegative()
    {
        Assert.Equal("00:00", IntelTime.Countdown(0));
        Assert.Equal("01:52", IntelTime.Countdown(112));
        Assert.Equal("59:59", IntelTime.Countdown(3599));
        Assert.Equal("1:02:03", IntelTime.Countdown(3723));
        Assert.Equal("00:00", IntelTime.Countdown(-5));
        var e = Event(startsAt: 100);
        Assert.Equal(2, IntelTime.SecondsLeft(e, 98.2)); // rounded up: the last second reads 1, zero means begun
        Assert.Equal(IntelStatus.Upcoming, IntelTime.Status(e, 99.99));
        Assert.Equal(IntelStatus.Underway, IntelTime.Status(e, 100));
        Assert.Equal(0, IntelTime.SecondsLeft(e, 500));
    }

    // ---- the test event, the command, the data files ----

    [Fact]
    public void TheTestEventCarriesEveryFieldAndIsMarkedAsATest()
    {
        var e = IntelMock.Test(now: 1000, where: new Vector2(3, 4));
        Assert.True(e.Test);
        Assert.Equal(1000 + IntelMock.LeadSeconds, e.StartsAt);
        Assert.Equal(e.StartsAt + IntelMock.StaysSeconds, e.EndsAt);
        Assert.Equal(new Vector2(3, 4), e.Where);
        Assert.NotEmpty(e.Name);
        Assert.NotEmpty(e.Description);
        Assert.NotEmpty(e.Risks);
        var back = IntelCodec.Decode(IntelCodec.Encode(new[] { e }, 1000, e.Id), 1000)!.Events[0]; // it fits the limits as written
        Assert.Equal(e.Name, back.Name);
        Assert.Equal(e.Description, back.Description);
        Assert.Equal(e.Risks, back.Risks);
    }

    [Fact]
    public void EventIntelIsAnAdminCommandWithAnOptionalClear()
    {
        var admin = new ChatContext(true, Array.Empty<string>());
        Assert.Equal(new[] { "intel" }, ChatCommands.Parse("/event INTEL", admin).Args);
        Assert.Equal(new[] { "intel", "clear" }, ChatCommands.Parse("/event intel clear", admin).Args);
        Assert.False(ChatCommands.Parse("/event intel remove", admin).Ok);
        Assert.False(ChatCommands.Parse("/event intel clear now", admin).Ok);
        Assert.Contains("admin", ChatCommands.Parse("/event intel", new ChatContext(false, Array.Empty<string>())).Error);
        Assert.Contains(ChatCommands.Hint("/event ", admin).Suggestions, s => s.Insert == "intel");
        Assert.Contains("TEST", ChatCommands.Choices(ChatCommands.Find("event")!, 0, admin).First(c => c.Value == "intel").Detail);
    }

    [Fact]
    public void TheShippedIntelFileIsValid()
    {
        var f = IntelFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "intel.json")));
        Assert.False(string.IsNullOrWhiteSpace(f.BaseBunker));
        Assert.Throws<DataFileException>(() => IntelFile.Parse("""{ "version": 1, "rotate_seconds": 0 }"""));
        Assert.Throws<DataFileException>(() => IntelFile.Parse("""{ "version": 1, "view_range_m": 1 }"""));
    }

    [Fact]
    public void TheIntelScreenIsFoundInBunkersJsonInGodotAxes()
    {
        const string json = """
        { "version": 1, "bunkers": [
          { "id": "wA", "floors": [ { "level": 0, "polygon": [[0,0],[4,0],[4,3],[0,3]], "z0": 100.0, "z1": 102.4 } ],
            "intel": { "x": 2.0, "y": 3.0, "dx": 0.0, "dy": -1.0, "z0": 99.0, "z1": 100.1, "width": 2.4 } },
          { "id": "wB", "floors": [ { "level": 0, "polygon": [[20,0],[24,0],[24,3],[20,3]], "z0": 100.0, "z1": 102.4 } ] } ] }
        """;
        var s = Shelters.Parse(json);
        var a = Assert.Single(s.IntelScreens);
        Assert.Equal("wA", a.BunkerId);
        Assert.Equal(new Vector2(2, -3), a.At); // north is -z
        Assert.Equal(new Vector2(0, 1), a.Facing); // facing south into the room
        Assert.Equal(1.1f, a.Height, 3);
        Assert.Equal(2.4f, a.Width);
        Assert.Empty(Shelters.Empty.IntelScreens);
        Assert.Throws<DataFileException>(() => Shelters.Parse(json.Replace("\"width\": 2.4", "\"width\": 0")));
    }
}

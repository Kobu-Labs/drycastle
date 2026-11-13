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
        Assert.Equal("pack:synty-military/SM_Bld_Hangar_Open_01", f.BaseAsset);
        Assert.False(string.IsNullOrWhiteSpace(f.BaseBunker)); // the fallback
        Assert.Throws<DataFileException>(() => IntelFile.Parse("""{ "version": 1, "rotate_seconds": 0 }"""));
        Assert.Throws<DataFileException>(() => IntelFile.Parse("""{ "version": 1, "view_range_m": 1 }"""));
    }

    // ---- the room on a placed object ----

    const string RoomJson = """{ "floor_m": 0.5, "at": [2.0, -16.0], "facing": [0, 1], "bottom_m": 1.0, "width_m": 3.0, "height_m": 1.1 }""";
    static IntelRoomFile Room(string json = RoomJson) => IntelFile.Parse($$"""{ "version": 1, "base_asset": "a", "room": {{json}} }""").Room!;
    static void Near(Vector2 expected, Vector2 actual, int places = 3) { Assert.Equal(expected.X, actual.X, places); Assert.Equal(expected.Y, actual.Y, places); }

    [Fact]
    public void AnUnturnedObjectCarriesTheScreenAtItsOwnOffsets()
    {
        var a = IntelRoom.Place("o-1", new Vector2(10, 20), 100f, 0f, 1f, Room());
        Assert.Equal("o-1", a.HostId);
        Near(new Vector2(12, 4), a.At);
        Near(new Vector2(0, 1), a.Facing); // local +z is still south
        Assert.Equal(101.5f, a.Y0, 3); // origin + floor + bottom edge
        Assert.Equal(102.6f, a.Y1, 3);
        Assert.Equal(1.1f, a.Height, 3);
        Assert.Equal(3.0f, a.Width);
    }

    [Fact]
    public void ATurnFollowsGodotsYRotation()
    {
        // 90 degrees counter-clockwise from above: local +z goes to +x (east), local +x to -z (north)
        var a = IntelRoom.Place("o", Vector2.Zero, 0f, 90f, 1f, Room());
        Near(new Vector2(-16, -2), a.At);
        Near(new Vector2(1, 0), a.Facing);
        Near(new Vector2(0, -1), IntelRoom.Rotate(new Vector2(1, 0), 90f));
        // a full turn and a turn beyond 180 degrees (the sign a loader's Euler angles would lose) come back the same
        Near(IntelRoom.Place("o", new Vector2(5, 5), 0f, 25f, 1f, Room()).At, IntelRoom.Place("o", new Vector2(5, 5), 0f, 385f, 1f, Room()).At);
        Near(IntelRoom.Place("o", new Vector2(5, 5), 0f, 200f, 1f, Room()).At, IntelRoom.Place("o", new Vector2(5, 5), 0f, -160f, 1f, Room()).At);
    }

    [Fact]
    public void ScaleMovesTheOffsetsButNotTheScreensSize()
    {
        var a = IntelRoom.Place("o", new Vector2(10, 20), 100f, 0f, 2f, Room());
        Near(new Vector2(14, -12), a.At);
        Assert.Equal(103f, a.Y0, 3); // 100 + (0.5 + 1.0) * 2
        Assert.Equal(3.0f, a.Width);
        Assert.Equal(1.1f, a.Height, 3);
        Near(new Vector2(12, 4), IntelRoom.Place("o", new Vector2(10, 20), 100f, 0f, 0f, Room()).At); // a scale that is no scale counts as 1
    }

    [Fact]
    public void TheScreenOfTheHangarAsPlacedHangsOnTheSideWallFacingAcrossTheHall()
    {
        // o-0582 in the village map (the west open hangar of the military base): local (537.706, 585.105) m, turned 332.5 degrees; ground z is -north
        var f = IntelFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "intel.json")));
        var origin = new Vector2(537.706f, -585.105f);
        var a = IntelRoom.Place("o-0582", origin, 150f, 332.5f, 1f, f.Room!);
        Near(IntelRoom.Rotate(new Vector2(1, 0), 332.5f), a.Facing); // local +x: toward the other hangar
        Assert.Equal(150f + 0.114f + 1.0f, a.Y0, 3);
        // in the object's own frame again: 13.4 m from its middle on the -x side wall, halfway along the hall (30.6 x 36.3 m)
        var rel = a.At - origin;
        Assert.Equal(-13.4f, Vector2.Dot(rel, a.Facing), 3);
        Assert.InRange(MathF.Abs(Vector2.Dot(rel, IntelRoom.Rotate(new Vector2(0, 1), 332.5f))), 0f, 0.01f);
        Assert.Empty(f.Room!.OpenParts); // both ends of the open hangar are open
    }

    [Fact]
    public void TheRoomOfIntelJsonIsChecked()
    {
        Assert.Equal(1.0f, Room().BottomM);
        Assert.Empty(Room().OpenParts);
        Assert.Throws<DataFileException>(() => IntelFile.Parse("""{ "version": 1, "base_asset": "a" }""")); // an asset needs its room
        Assert.Throws<DataFileException>(() => Room("""{ "at": [1], "facing": [0, 1] }"""));
        Assert.Throws<DataFileException>(() => Room("""{ "at": [1, 1], "facing": [0, 2] }"""));
        Assert.Throws<DataFileException>(() => Room("""{ "at": [1, 1], "facing": [0, 1], "width_m": 1.0 }"""));
        Assert.Throws<DataFileException>(() => Room("""{ "at": [1, 1], "facing": [0, 1], "height_m": 0.5 }"""));
        Assert.Throws<DataFileException>(() => Room("""{ "at": [1, 1], "facing": [0, 1], "bottom_m": -1 }"""));
        Assert.Null(IntelFile.Parse("""{ "version": 1, "base_bunker": "w1" }""").Room); // the bunker alone is a valid file too
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
        Assert.Equal("wA", a.HostId);
        Assert.Equal(new Vector2(2, -3), a.At); // north is -z
        Assert.Equal(new Vector2(0, 1), a.Facing); // facing south into the room
        Assert.Equal(1.1f, a.Height, 3);
        Assert.Equal(2.4f, a.Width);
        Assert.Empty(Shelters.Empty.IntelScreens);
        Assert.Throws<DataFileException>(() => Shelters.Parse(json.Replace("\"width\": 2.4", "\"width\": 0")));
    }
}

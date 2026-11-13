using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class ChurchShellTests
{
    /// <summary>A church whose doorway is at (10, 20) in local metres on the floor 100.25, facing north (outward); the nave is y 10..19.8, the chancel y 2..10 (one room).</summary>
    const string Json = """
    {
      "doorway": { "pos": [10, 20, 100.25], "yaw": 0, "width": 2.0, "height": 2.65 },
      "rooms": [
        { "polygon": [[8,10],[12,10],[12,19.8],[8,19.8]], "z0": 100.0, "z1": 107.4 },
        { "polygon": [[9,2],[11,2],[11,10],[9,10]], "z0": 100.0, "z1": 105.2 }
      ]
    }
    """;

    static readonly Vector3 Nave = new(10, 101, -15), Chancel = new(10, 101, -5), Outside = new(10, 101, -25);

    static void Near(Vector3 expected, Vector3 actual) => Assert.True((expected - actual).Length() < 1e-4f, $"{actual} is not {expected}");

    [Fact]
    public void TheDoorwayAndTheRoomsAreInGodotAxes()
    {
        var c = ChurchShell.Parse(Json)!;
        Assert.Equal(new Vector3(10, 100.25f, -20), c.DoorwayPos);
        Near(new Vector3(0, 0, -1), c.Out); // north is -z
        Near(new Vector3(1, 0, 0), c.Right); // east: the right of someone outside who faces the church
        Assert.True(c.InRooms(Nave));
        Assert.True(c.InRooms(Chancel));
        Assert.False(c.InRooms(Outside));
    }

    [Fact]
    public void AnEastFacingDoorwayTurnsTheFrame()
    {
        var c = ChurchShell.Parse(Json.Replace("\"yaw\": 0", "\"yaw\": 90"))!;
        Near(new Vector3(1, 0, 0), c.Out);
    }

    [Fact]
    public void ABodyInTheDoorwayIsMovedToTheNearerSide()
    {
        var c = ChurchShell.Parse(Json)!;
        var outer = c.PushOutOfDoorway(new Vector3(10.4f, 100.25f, -20.05f), 0.35f)!.Value; // 0.05 m out of the middle of the doorway
        Assert.True(Vector3.Dot(outer - c.DoorwayPos, c.Out) > 0.3f);
        Assert.Equal(10.4f, outer.X, 3); // only along the depth
        var inner = c.PushOutOfDoorway(new Vector3(9.5f, 100.25f, -19.9f), 0.35f)!.Value; // 0.1 m inside
        Assert.True(Vector3.Dot(inner - c.DoorwayPos, c.Out) < -0.3f);
        Assert.Null(c.PushOutOfDoorway(new Vector3(10, 100.25f, -21f), 0.35f)); // clear, a metre out
        Assert.Null(c.PushOutOfDoorway(new Vector3(12.5f, 100.25f, -20f), 0.35f)); // beside the jamb
        Assert.Null(c.PushOutOfDoorway(new Vector3(10, 103f, -20f), 0.35f)); // above the lintel
    }

    [Fact]
    public void TheChurchIsOneShelterAndTheDoorsDoNotChangeIt()
    {
        var shelters = Shelters.Empty.WithChurch(ChurchShell.Parse(Json));
        Assert.Equal(2, shelters.Rooms.Count);
        Assert.True(shelters.Reaches(Nave, Chancel)); // nave and chancel are one room
        Assert.False(shelters.Reaches(Outside, Nave));
        Assert.False(shelters.Reaches(Nave, Outside));
        Assert.True(shelters.Reaches(Outside, new Vector3(30, 101, -25)));
    }

    [Fact]
    public void AMapWithoutADoorwayHasNoChurchToClose()
    {
        Assert.Null(ChurchShell.FromJson("""{"door": {"x": 1, "y": 2, "z": 0}}"""));
        Assert.Null(ChurchShell.FromJson(""));
        Assert.Same(Shelters.Empty, Shelters.Empty.WithChurch(null));
        Assert.Throws<DataFileException>(() => ChurchShell.Parse("""{"doorway": {"pos": [1,2], "yaw": 0, "width": 2, "height": 2}}"""));
    }

    [Fact]
    public void TheDoorEventIsOneByte()
    {
        Assert.True(ChurchDoorCodec.Decode(ChurchDoorCodec.Encode(true)));
        Assert.False(ChurchDoorCodec.Decode(ChurchDoorCodec.Encode(false)));
        Assert.Null(ChurchDoorCodec.Decode(new byte[] { 7 }));
    }

    [Fact]
    public void ChurchIsAnAdminCommand()
    {
        var admin = new ChatContext(true, Array.Empty<string>());
        Assert.Equal(new[] { "open" }, ChatCommands.Parse("/church OPEN", admin).Args);
        Assert.True(ChatCommands.Parse("/church", admin).Ok);
        Assert.False(ChatCommands.Parse("/church ajar", admin).Ok);
        Assert.False(ChatCommands.Parse("/church open", new ChatContext(false, Array.Empty<string>())).Ok);
    }
}

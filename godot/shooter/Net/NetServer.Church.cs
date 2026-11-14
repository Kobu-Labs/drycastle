using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The church on the server (ADR 0019): the doors and the shelter. The doors are a two-state door in the doorway, closed by default; their box lives
/// in <see cref="ChurchDoorNode"/> (made with the map, on the World layer, so players, bullets and citizens stop at it), the state is replicated as
/// <see cref="EventKind.ChurchDoor"/> (a joiner gets it when the doors are open). The event code drives them through <see cref="ChurchDoors"/>;
/// <c>/church open|close</c> is the admin's test. The church rooms are loaded into the <see cref="Shelters"/> (<see cref="MapShelters"/>).
/// </summary>
public partial class NetServer
{
    ChurchShell? _mapChurch;
    bool _mapChurchTried;
    ChurchDoors? _churchDoors;
    bool _churchOpen;

    /// <summary>The church of the map (church.json with a doorway), or null: no map, an older church.json, or a broken one (logged).</summary>
    ChurchShell? MapChurch
    {
        get
        {
            if (_mapChurchTried) return _mapChurch;
            _mapChurchTried = true;
            if (ArenaMode || string.IsNullOrEmpty(MapDir)) return null;
            var path = Path.Combine(MapDir, "church.json");
            if (!File.Exists(path)) return null;
            _mapChurch = ChurchShell.FromJsonOrNull(File.ReadAllText(path), m => GD.PrintErr($"server: {m}; the church has no doors and gives no shelter"));
            return _mapChurch;
        }
    }

    /// <summary>The church doors, for the event code. Works without a church on the map too (<see cref="ChurchDoors.Exists"/> is then false: the state is kept, nothing moves).</summary>
    public ChurchDoors ChurchDoors => _churchDoors ??= new ChurchDoors(this);

    /// <summary>The joiner's catch-up: the doors are closed by default, so only open doors are told.</summary>
    void SendChurchOnJoin(Client c)
    {
        if (_churchOpen) Send(c, new EventMsg((byte)EventKind.ChurchDoor, ChurchDoorCodec.Encode(true)));
    }

    /// <summary>"/church [open|close]": opens or closes the doors, or says which they are.</summary>
    void ChatChurch(Client c, IReadOnlyList<string> args)
    {
        if (!ChurchDoors.Exists) { ChatTo(c, ChatLineKind.Error, "This map has no church doors (no church.json with a doorway yet)."); return; }
        if (args.Count == 0) { ChatTo(c, ChatLineKind.Info, $"The church doors are {(ChurchDoors.IsOpen ? "open" : "closed")}; {ChurchDoors.PlayersInside().Count} inside."); return; }
        if (args[0] == "open") ChurchDoors.Open(); else ChurchDoors.Close();
        ChatToAll(ChatLineKind.System, "", $"{c.Name} {(ChurchDoors.IsOpen ? "opened" : "closed")} the church doors.");
    }

    internal bool HasChurch => MapChurch != null;
    internal bool ChurchOpen => _churchOpen;
    internal IReadOnlyList<ushort> PlayersInChurchRooms() => PlayersInChurch();

    internal void SetChurchDoorsOpen(bool open)
    {
        if (open == _churchOpen) return;
        _churchOpen = open;
        ChurchDoorNode.Current?.SetOpen(open, animate: false); // the box first: nobody can walk into the doorway behind the push
        if (!open) PushOutOfDoorway();
        BroadcastEvent(EventKind.ChurchDoor, ChurchDoorCodec.Encode(open));
        GD.Print($"server: church doors {(open ? "open" : "closed")}");
    }

    /// <summary>A door never closes through a body: whoever stands in the doorway is moved to the nearer side (a ride in a vehicle is left alone).</summary>
    void PushOutOfDoorway()
    {
        if (MapChurch is not { } church) return;
        foreach (var c in _byId.Values)
        {
            if (!c.Joined || c.Car != null || _match.Get(c.Id)?.Life != PlayerLife.Alive) continue;
            var p = c.Body.GlobalPosition;
            if (church.PushOutOfDoorway(new NVec3(p.X, p.Y, p.Z), MoveConsts.CapsuleRadius) is not { } moved) continue;
            c.Body.Teleport(new Vector3(moved.X, moved.Y, moved.Z));
            c.Inputs.Clear();
            _match.SetPosition(c.Id, moved);
            GD.Print($"server: {c.Name} #{c.Id} moved out of the church doorway");
        }
    }

    List<ushort> PlayersInChurch()
    {
        var list = new List<ushort>();
        if (MapChurch is not { } church) return list;
        foreach (var c in _byId.Values)
        {
            if (!c.Joined || _match.Get(c.Id)?.Life != PlayerLife.Alive) continue;
            var p = c.Body.GlobalPosition;
            if (church.InRooms(new NVec3(p.X, p.Y, p.Z))) list.Add(c.Id);
        }
        return list;
    }
}

/// <summary>
/// The server's handle on the church doors (ADR 0019), for the Holy Mass event: <see cref="Open"/> and <see cref="Close"/> change the state at
/// once on the server (the box in the doorway, the shelter does not change) and tell every client; closing moves a body standing in the doorway to
/// the nearer side first, but locks in everyone else (the demon fight). Closed by default.
/// </summary>
public sealed class ChurchDoors
{
    readonly NetServer _s;
    internal ChurchDoors(NetServer server) => _s = server;

    /// <summary>The map has a church with a doorway. Without one the state is still kept, but nothing is solid and clients draw nothing.</summary>
    public bool Exists => _s.HasChurch;

    public bool IsOpen => _s.ChurchOpen;
    public void Open() => _s.SetChurchDoorsOpen(true);
    public void Close() => _s.SetChurchDoorsOpen(false);

    /// <summary>The ids of the living players (bots too) whose feet are in the nave or the chancel (the <c>rooms</c> of church.json). Empty without a church.</summary>
    public IReadOnlyList<ushort> PlayersInside() => _s.PlayersInChurchRooms();
}

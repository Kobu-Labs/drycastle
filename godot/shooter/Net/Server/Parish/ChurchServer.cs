using Godot;
using Drycastle.Shooter.Churches;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Churches;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Parish;

/// <summary>
/// The church on the server (ADR 0019): the doors and the shelter. The doors are a two-state door in the doorway, closed by default; their box lives
/// in <see cref="ChurchDoorNode"/> (made with the map, on the World layer, so players, bullets and citizens stop at it), the state is replicated as
/// <see cref="EventKind.ChurchDoor"/> (a joiner gets it when the doors are open). What the doors should do is decided in ONE place, <see cref="ChurchDoorPolicy"/>
/// (ShooterCore): the Mass and the demon name a <see cref="DoorReason"/> through <see cref="DoorsFor"/>, and <see cref="Apply"/> moves the box once.
/// <c>/church open|close</c> is the admin's test. The church rooms are loaded into the shelters (<see cref="Shell"/>).
/// </summary>
internal sealed class ChurchServer
{
    readonly ServerContext _ctx;
    readonly bool _arenaMode;
    readonly string _mapDir;
    LazyInit<ChurchShell> _shell;
    /// <summary>The decider of the doors (open/closed, the close-when-empty latch, the demon fight's grace clock).</summary>
    readonly ChurchDoorPolicy _policy = new();

    public ChurchServer(ServerContext ctx, bool arenaMode, string mapDir) { _ctx = ctx; _arenaMode = arenaMode; _mapDir = mapDir; }

    /// <summary>The church of the map (church.json with a doorway), or null: no map, an older church.json, or a broken one (logged).</summary>
    public ChurchShell? Shell
    {
        get
        {
            if (!_shell.Begin()) return _shell.Value;
            if (_arenaMode || string.IsNullOrEmpty(_mapDir)) return null;
            var path = Path.Combine(_mapDir, "church.json");
            if (!File.Exists(path)) return null;
            if (ChurchShell.FromJsonOrNull(File.ReadAllText(path), m => GD.PrintErr($"server: {m}; the church has no doors and gives no shelter")) is { } church) _shell.Ready(church);
            return _shell.Value;
        }
    }

    /// <summary>The map has a church with a doorway. Without one the state is still kept, but nothing is solid and clients draw nothing.</summary>
    public bool Exists => Shell != null;

    public bool IsOpen => _policy.IsOpen;

    /// <summary>The joiner's catch-up: the doors are closed by default, so only open doors are told.</summary>
    public void SendOnJoin(ServerClient c)
    {
        if (_policy.IsOpen) _ctx.SendEvent(c, EventKind.ChurchDoor, ChurchDoorCodec.Encode(true));
    }

    /// <summary>"/church [open|close]": opens or closes the doors, or says which they are.</summary>
    public void Chat(ServerClient c, IReadOnlyList<string> args)
    {
        if (!Exists) { _ctx.ChatTo(c, ChatLineKind.Error, "This map has no church doors (no church.json with a doorway yet)."); return; }
        if (args.Count == 0) { _ctx.ChatTo(c, ChatLineKind.Info, $"The church doors are {(IsOpen ? "open" : "closed")}; {PlayersInside().Count} inside."); return; }
        if (_policy.SetByAdmin(args[0] == "open")) Apply(); // the admin's word is one-off: a pending close-when-empty still fires
        _ctx.ChatTo(c, ChatLineKind.Info, $"{c.Name} {(IsOpen ? "opened" : "closed")} the church doors.");
    }

    /// <summary>A decider (the Mass, the demon) names why the doors move; the policy decides, the world follows.</summary>
    public void DoorsFor(DoorReason reason)
    {
        if (_policy.Apply(reason)) Apply();
    }

    /// <summary>The Mass changed phase: gather opens, void slams shut, the end shuts them once the church is empty.</summary>
    public void FollowMass(MassPhase p)
    {
        if (p == MassPhase.Gather) DoorsFor(DoorReason.MassGathering);
        else if (p == MassPhase.Void) DoorsFor(DoorReason.MassVoid);
        else if (p == MassPhase.Idle) DoorsFor(DoorReason.MassOver);
    }

    /// <summary>The demon is made: from now the doors follow the fight.</summary>
    public void FightBegins() => _policy.FightBegins();

    /// <summary>Once a tick: the close that waits for an empty church (the head count is only taken while one waits).</summary>
    public void Tick()
    {
        if (_policy.TickDeferredClose(() => PlayersInside().Count)) Apply();
    }

    /// <summary>The fight's door rule: locked while someone is inside, open and waiting when nobody is. True when the doors moved (<paramref name="wasOpen"/>: what they were).</summary>
    public bool TickFight(float dt, int inside, float graceSeconds, out bool wasOpen)
    {
        wasOpen = IsOpen;
        if (!_policy.TickFight(dt, inside, graceSeconds)) return false;
        Apply();
        return true;
    }

    /// <summary>The ONE place the doors move in the world: the policy's state goes to the box, the push out of the doorway, and every client.</summary>
    void Apply()
    {
        var open = _policy.IsOpen;
        ChurchDoorNode.Current?.SetOpen(open, animate: false); // the box first: nobody can walk into the doorway behind the push
        if (!open) PushOutOfDoorway();
        _ctx.Broadcast(EventKind.ChurchDoor, ChurchDoorCodec.Encode(open));
        GD.Print($"server: church doors {(open ? "open" : "closed")}");
    }

    /// <summary>A door never closes through a body: whoever stands in the doorway is moved to the nearer side (a ride in a vehicle is left alone).</summary>
    void PushOutOfDoorway()
    {
        if (Shell is not { } church) return;
        foreach (var c in _ctx.Players.Values)
        {
            if (!c.Joined || c.Car != null || !_ctx.IsAlive(c.Id)) continue;
            if (church.PushOutOfDoorway(c.Body.GlobalPosition.ToNumerics(), MoveConsts.CapsuleRadius) is not { } moved) continue;
            c.Body.Teleport(new Vector3(moved.X, moved.Y, moved.Z));
            c.Inputs.Clear();
            _ctx.Match.SetPosition(c.Id, moved);
            GD.Print($"server: {c.Name} #{c.Id} moved out of the church doorway");
        }
    }

    /// <summary>The ids of the living players (bots too) whose feet are in the nave or the chancel (the <c>rooms</c> of church.json). Empty without a church.</summary>
    public List<ushort> PlayersInside()
    {
        var list = new List<ushort>();
        if (Shell is not { } church) return list;
        foreach (var c in _ctx.Players.Values)
        {
            if (!c.Joined || !_ctx.IsAlive(c.Id)) continue;
            if (church.InRooms(c.Body.GlobalPosition.ToNumerics())) list.Add(c.Id);
        }
        return list;
    }
}

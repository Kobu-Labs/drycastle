using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Net.Server.Careers;
using Drycastle.Shooter.Net.Server.Terrain;
using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.ChatRoom;
using Drycastle.Shooter.Net.Server.Fauna;
using Drycastle.Shooter.Net.Server.Playground;
using Drycastle.Shooter.Net.Server.Profiles;
using Drycastle.Shooter.Net.Server.Seating;
using Drycastle.Shooter.Net.Server.Statuses;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.Shooter.Net.Server.VillageRadio;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Server.Sessions;

/// <summary>Who is in the match: a client joins (body, welcome, everything the joiner is told, the spawn) and leaves, and the free player ids for connections and bots come from here.</summary>
internal sealed class PlayerRoster
{
    readonly ServerContext _ctx;
    readonly ServerConfig _config;
    readonly ENetConnection _host;
    readonly Dictionary<ushort, ServerClient> _byId;
    readonly PlayerIds _ids;
    readonly SnapshotHistory _sentStates;
    readonly WorldHistory _sentWorld;
    readonly ProgressionServer _progress;
    readonly MapAreas _areas;
    readonly AnimalWorld _animals;
    readonly ProfileServer _profiles;
    readonly CompanionServer _companions;
    readonly SandboxServer _sandbox;
    readonly ChatServer _chat;
    readonly StreakServer _streaks;
    readonly StatusServer _status;
    readonly RozhlasServer _rozhlas;
    readonly Sitters _sitters;
    readonly CarSeats _seats;
    readonly IReadOnlyList<Action<ServerClient>> _tellJoiner;

    /// <param name="tellJoiner">What the joiner is told about the world, in this order: each feature's state a client has to see (open doors, broken glass, who wears what).</param>
    public PlayerRoster(ServerContext ctx, ServerConfig config, ENetConnection host, Dictionary<ushort, ServerClient> byId, PlayerIds ids, SnapshotHistory sentStates, WorldHistory sentWorld,
        ProgressionServer progress, MapAreas areas, AnimalWorld animals, ProfileServer profiles, CompanionServer companions, SandboxServer sandbox, ChatServer chat,
        StreakServer streaks, StatusServer status, RozhlasServer rozhlas, Sitters sitters, CarSeats seats, IReadOnlyList<Action<ServerClient>> tellJoiner)
    {
        _ctx = ctx; _config = config; _host = host; _byId = byId; _ids = ids; _sentStates = sentStates; _sentWorld = sentWorld;
        _progress = progress; _areas = areas; _animals = animals; _profiles = profiles; _companions = companions; _sandbox = sandbox; _chat = chat;
        _streaks = streaks; _status = status; _rozhlas = rozhlas; _sitters = sitters; _seats = seats; _tellJoiner = tellJoiner;
    }

    /// <summary>A free player id for a connection or a bot (0 when there is none).</summary>
    public ushort NewPlayerId() => _ids.Allocate(id => _ctx.Connections.Any(o => o.Id == id) || _byId.ContainsKey(id));

    /// <summary>Joins a client: body, welcome, profile, the join broadcast and the spawn. Called once the client has a profile.</summary>
    public void Join(ServerClient c)
    {
        var clock = _config.Clock;
        // health, death and respawn belong to the match state machine, not the body
        _progress.ApplyLoadout(c);
        c.Recoil = new RecoilState(c.Id);
        c.Breath = new BreathState(c.Id);
        c.Worn = new Equipment(_config.Items);
        c.Bag = new Bag();
        ItemRules.IssueStandard(_config.Items, c.Bag, ProfileServer.OwnedItems(c)); // every player has the standard items (the binoculars) from the start
        c.Body = new Player { UseCamera = false, AutoSim = false, CaptureMouse = false, Authoritative = false, Name = $"P{c.Id}", Water = _config.Water, Ladders = _areas.Ladders, Bodies = _animals.Crowd };
        _config.World.AddChild(c.Body);
        c.Body.Teleport(Vector3.Zero);
        c.Session.Enter(SessionPhase.Joined);
        _byId[c.Id] = c;
        c.Enc = new SnapshotEncoder(_sentStates);
        c.WEnc = new WorldSnapshotEncoder(history: _sentWorld);
        _profiles.Send(c);
        _companions.SendRoster(c); // the Companions they can pick and the one that is out (the Equipment screen's picker)
        _sandbox.SendStatus(c); // the answer to a sandbox request (#156), before the spawn
        _chat.SendStatus(c); // admin or not, and the vehicles /spawn takes
        _ctx.Send(c, new WelcomeMsg(Wire.ProtocolVersion, c.Id, Wire.TickRate, _ctx.Tick,
            (ushort)(clock.StartMinute), (uint)Math.Round(clock.DayLengthSeconds * 1000), clock.Frozen));
        foreach (var o in _byId.Values)
        {
            _ctx.Send(c, new JoinMsg(o.Id, o.Name));
            if (o != c) _ctx.Send(o, new JoinMsg(c.Id, c.Name));
        }
        foreach (var w in _ctx.Match.Entities.Snapshot()) _ctx.Send(c, new EventMsg((byte)EventKind.WorldSpawn, WorldCodec.EncodeSpawn(w.Id, w.Type, w.State)));
        foreach (var tell in _tellJoiner) tell(c);
        _ctx.Match.AddPlayer(c.Id, c.Name, c.Sandbox, c.IsBot); // spawns the player (event) and broadcasts the scoreboard
        _sandbox.Announce(c); // sandbox name tags both ways, and the held streak (#156)
        _host.Flush();
        GD.Print($"server: {c.Name} joined as #{c.Id} ({_byId.Count} players)");
    }

    /// <summary>Takes a joined player out of the match: a dropped connection, or a bot that is no longer wanted.</summary>
    public void Leave(ServerClient c)
    {
        _streaks.Leave(c.Id);
        _companions.Leave(c.Id);
        _status.Leave(c.Id);
        _rozhlas.OnPlayerLeft(c.Id);
        _sitters.StandUp(c.Id); _sitters.Forget(c.Id);
        _seats.Detach(c);
        c.Body.QueueFree();
        _byId.Remove(c.Id);
        foreach (var o in _byId.Values) _ctx.Send(o, new LeaveMsg(c.Id));
        _ctx.Match.RemovePlayer(c.Id);
        GD.Print($"server: {c.Name} #{c.Id} left");
    }
}

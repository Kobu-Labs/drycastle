using Godot;
using Drycastle.Shooter.Buildings;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.RozhlasFeed;
using Drycastle.ShooterCore.Weapons;


namespace Drycastle.Shooter.Net.Server.VillageRadio;

/// <summary>
/// The Obecny Urad on the server (docs/events/README.md, "Rozhlas detour"): <c>urad.json</c> from the map folder, next to church.json (<see cref="Urad"/>: the doorway, the volume, the worker seats, the dictation table), the workers
/// (<see cref="WorkerCrowd"/>: mayor, clerks, announcer as citizen entities) and the door. The workers exist exactly while a detour is open: they sit down at their spots when the loudspeaker goes Idle to OnAir and are
/// all removed (corpses included) when it is Idle again, fresh every broadcast. They are hit targets like the Mass citizens under ids of their own and kill
/// in one hit, with no score and no kill feed. The announcer's death is the hijack (<see cref="RozhlasBroadcast.AnnouncerKilled"/>); any other worker's changes nothing.
/// <para>
/// The door is open exactly while a rozhlas detour is open (<see cref="RozhlasBroadcast.DetourOpen"/>: OnAir or Hijacked), shut when the loudspeaker is Idle. The state is that phase, so there is no machine of its own: it moves the box
/// (<see cref="UradDoorNode"/>, on the World layer in the doorway of urad.json, made with the map) and tells everyone (<see cref="EventKind.UradDoor"/>, also to a joiner while it is open). When it shuts, nobody stays locked in for the
/// 10 minutes to the next broadcast: every living player in an interior room or in the doorway is put <see cref="Urad.EvictOutsideM"/> outside it (<see cref="Urad.EvictionPoint"/>).
/// </para>
/// A map without urad.json has no Urad (logged once): no volume, no workers, no table, no door.
/// </summary>
internal sealed class UradServer
{
    readonly ServerContext _ctx;
    readonly bool _arenaMode;
    readonly string _mapDir;
    readonly HolyMassFile _holyMass;
    readonly Func<CitizenHitRouter> _citizenHits;

    LazyInit<Urad> _urad;
    WorkerCrowd? _workers;
    bool _doorOpen;

    /// <param name="citizenHits">The router the match asks about citizen hit ids: the Mass crowd and the workers are each added when they exist.</param>
    public UradServer(ServerContext ctx, bool arenaMode, string mapDir, HolyMassFile holyMass, Func<CitizenHitRouter> citizenHits)
    {
        _ctx = ctx; _arenaMode = arenaMode; _mapDir = mapDir; _holyMass = holyMass; _citizenHits = citizenHits;
    }

    /// <summary>A worker was killed by a player (all roles). The announcer's death has already started the hijack when this fires.</summary>
    public event Action<CitizenKilled>? WorkerKilled;

    /// <summary>urad.json (null when the map has none or it cannot be read, which is logged once: no volume, no workers, no door).</summary>
    public Urad? Urad => Ensure();

    /// <summary>Whether a point (Godot axes) is inside the Urad: within a bar's footprint and between its floor and ceiling. False without urad.json.</summary>
    public bool Contains(Vector3 p) => Ensure()?.Contains(p.ToNumerics()) == true;

    /// <summary>The dictation table's position (where the set is), on the floor; null without urad.json.</summary>
    public Vector3? Table => Ensure() is { } u ? u.Table.Pos.ToGodot() : null;

    Urad? Ensure()
    {
        if (!_urad.Begin()) return _urad.Value;
        if (_arenaMode || string.IsNullOrEmpty(_mapDir)) { GD.Print("server: no map folder; no Urad volume, no workers, no door"); return null; }
        var path = Path.Combine(_mapDir, "urad.json");
        if (!File.Exists(path)) { GD.Print($"server: no {path}; no Urad volume, no workers, no door"); return null; }
        if (Urad.FromJsonOrNull(File.ReadAllText(path), m => GD.PrintErr($"server: {m}; no Urad volume, no workers, no door")) is { } urad) _urad.Ready(urad);
        return _urad.Value;
    }

    /// <summary>Called once when the loudspeaker is made: the workers follow its phase, and the announcer's death is the hijack.</summary>
    public void AttachWorkers(RozhlasBroadcast r)
    {
        _workers = new WorkerCrowd(_holyMass.Citizens, _ctx.Match.Entities, System.Environment.TickCount);
        _workers.Attach(r, () => Ensure()?.Workers ?? new List<UradWorkerSpot>());
        _workers.Killed += k =>
        {
            GD.Print($"server: #{k.Killer} killed the rozhlas {k.Role.ToString().ToLowerInvariant()}");
            WorkerKilled?.Invoke(k);
        };
        r.PhaseChanged += (from, to) => { if (from == RozhlasPhase.Idle && to == RozhlasPhase.OnAir) GD.Print($"server: rozhlas: {_workers.Count} Urad workers sit down"); };
        _citizenHits().Add(_workers);
    }

    /// <summary>A round reset cleared the entities: workers of a detour still open sit down again.</summary>
    public void RoundReset() => _workers?.Resync();

    /// <summary>The workers are hit targets, sitting still.</summary>
    public void AddTargets(List<HitTarget> into) => _workers?.AddTargets(into);

    /// <summary>The ONE place the door moves in the world: the box first (nobody can walk into the doorway behind the move), then whoever is inside, then every client. Only a change does anything.</summary>
    public void SetDoor(bool open)
    {
        if (open == _doorOpen || Ensure() == null) return;
        _doorOpen = open;
        UradDoorNode.Current?.SetOpen(open, animate: false);
        if (!open) EvictAll();
        _ctx.Broadcast(EventKind.UradDoor, UradDoorCodec.Encode(open));
        GD.Print($"server: Urad door {(open ? "open" : "closed")}");
    }

    /// <summary>The joiner's catch-up: the door is shut by default, so only an open door is told.</summary>
    public void SendDoorOnJoin(ServerClient c)
    {
        if (_doorOpen) _ctx.SendEvent(c, EventKind.UradDoor, UradDoorCodec.Encode(true));
    }

    /// <summary>The door shut: whoever is in the Urad or its doorway stands outside it (a ride in a vehicle is left alone).</summary>
    void EvictAll()
    {
        if (Ensure() is not { } urad) return;
        foreach (var c in _ctx.Players.Values)
        {
            if (!c.Joined || c.Car != null || !_ctx.IsAlive(c.Id)) continue;
            if (urad.EvictionPoint(c.Body.GlobalPosition.ToNumerics(), MoveConsts.CapsuleRadius) is not { } to) continue;
            c.Body.Teleport(to.ToGodot());
            c.Inputs.Clear();
            _ctx.Match.SetPosition(c.Id, to);
            GD.Print($"server: {c.Name} #{c.Id} shown out of the Urad");
        }
    }
}

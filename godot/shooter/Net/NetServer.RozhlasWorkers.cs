using Godot;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.RozhlasFeed;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The Obecny Urad on the server (docs/events/README.md, "Rozhlas detour"): <c>urad.json</c> from the map folder, next to church.json (<see cref="Urad"/>: the doorway, the volume, the worker seats, the dictation table) and the workers
/// (<see cref="WorkerCrowd"/>: mayor, clerks, announcer as citizen entities). They exist exactly while a detour is open: they sit down at their spots when the loudspeaker goes Idle to OnAir and are
/// all removed (corpses included) when it is Idle again, fresh every broadcast. They are hit targets like the Mass citizens (<see cref="LiveTargets"/>) under ids of their own and kill
/// in one hit, with no score and no kill feed. The announcer's death is the hijack (<see cref="RozhlasBroadcast.AnnouncerKilled"/>); any other worker's changes nothing.
/// <para>
/// A map without urad.json has no Urad (logged once): no volume, no workers, no table, no door. Later slices use <see cref="UradContains"/> (which shots, explosions and deaths the live feed
/// rebroadcasts), <see cref="UradTable"/> (where the set is) and <see cref="UradWorkerKilled"/>; the door is NetServer.RozhlasDoor.cs.
/// </para>
/// </summary>
public partial class NetServer
{
    LazyInit<Urad> _urad;
    WorkerCrowd? _workers;
    CitizenHitRouter? _citizenHits;

    /// <summary>urad.json (null when the map has none or it cannot be read, which is logged once: no volume, no workers, no door).</summary>
    public Urad? Urad => EnsureUrad();

    /// <summary>Whether a point (Godot axes) is inside the Urad: within a bar's footprint and between its floor and ceiling. False without urad.json.</summary>
    public bool UradContains(Vector3 p) => EnsureUrad()?.Contains(N(p)) == true;

    /// <summary>The dictation table's position (where the set is), on the floor; null without urad.json.</summary>
    public Vector3? UradTable => EnsureUrad() is { } u ? V(u.Table.Pos) : null;

    /// <summary>The workers of the detour, null until the first one opens.</summary>
    public WorkerCrowd? Workers => _workers;

    /// <summary>A worker was killed by a player (all roles). The announcer's death has already started the hijack when this fires.</summary>
    public event Action<CitizenKilled>? UradWorkerKilled;

    static Vector3 V(NVec3 v) => new(v.X, v.Y, v.Z);

    Urad? EnsureUrad()
    {
        if (!_urad.Begin()) return _urad.Value;
        if (ArenaMode || string.IsNullOrEmpty(MapDir)) { GD.Print("server: no map folder; no Urad volume, no workers, no door"); return null; }
        var path = Path.Combine(MapDir, "urad.json");
        if (!File.Exists(path)) { GD.Print($"server: no {path}; no Urad volume, no workers, no door"); return null; }
        if (Urad.FromJsonOrNull(File.ReadAllText(path), m => GD.PrintErr($"server: {m}; no Urad volume, no workers, no door")) is { } urad) _urad.Ready(urad);
        return _urad.Value;
    }

    /// <summary>The router the match asks about citizen hit ids: the Mass crowd and the workers are each added when they exist.</summary>
    CitizenHitRouter CitizenHits()
    {
        if (_citizenHits == null) _match.Citizens = _citizenHits = new CitizenHitRouter();
        return _citizenHits;
    }

    /// <summary>Called once from <see cref="EnsureRozhlas"/>: the workers follow the loudspeaker's phase, and the announcer's death is the hijack.</summary>
    void EnsureRozhlasWorkers(RozhlasBroadcast r)
    {
        _workers = new WorkerCrowd(HolyMass.Citizens, _match.Entities, System.Environment.TickCount);
        _workers.Attach(r, WorkerSpots);
        _workers.Killed += k =>
        {
            GD.Print($"server: #{k.Killer} killed the rozhlas {k.Role.ToString().ToLowerInvariant()}");
            UradWorkerKilled?.Invoke(k);
        };
        r.PhaseChanged += (from, to) => { if (from == RozhlasPhase.Idle && to == RozhlasPhase.OnAir) GD.Print($"server: rozhlas: {_workers.Count} Urad workers sit down"); };
        CitizenHits().Add(_workers);
    }

    /// <summary>Where the workers sit now: the seats of urad.json (none without it).</summary>
    IReadOnlyList<UradWorkerSpot> WorkerSpots() => EnsureUrad()?.Workers ?? new List<UradWorkerSpot>();

    /// <summary>A round reset cleared the entities: workers of a detour still open sit down again.</summary>
    void WorkersAfterRoundReset() => _workers?.Resync();
}

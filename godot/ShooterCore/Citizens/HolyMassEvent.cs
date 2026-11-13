using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// Where the Holy Mass is. Idle: nothing runs. Gather: the citizens walk in and sit. Mass: the chant runs and the Mass progresses while the pastor
/// is there. Completed: the Mass is done (the blessed are chosen; citizens stand and clap, the pastor flexes) and the church is closing. Void: the
/// pastor was killed too often and turns (the demon's cue); no blessing this event, and it stays until the event is ended.
/// </summary>
public enum MassPhase : byte { Idle = 0, Gather = 1, Mass = 2, Completed = 3, Void = 4 }

/// <summary>
/// The Holy Mass event: the timeline, the pastor and the monks, the seats players take, and the blessing. Godot-free; the server calls
/// <see cref="Update"/> every tick and wires the events to the world. It owns no clock beyond the seconds it is given.
/// <para>
/// Seats: citizens fill every seat except <see cref="MassConfig.PlayerSeats"/> that are kept for players. A player may take any seat no citizen has
/// (a kept one, or one a killed citizen left once its body is gone); a seat a citizen is walking to or sits in cannot be taken. Standing spots (the
/// kneeling ones) are never a player's.
/// </para>
/// <para>
/// Mass time is the time the Mass progressed (it stops while the pastor is gone, so a pause costs nobody). A player is blessed when seated at the end,
/// for at least <see cref="MassConfig.BlessedShare"/> of the progress so far, and not a sinner (killed a citizen, monk or pastor during the event).
/// Ending the event early with <see cref="Conclude"/> measures against the progress so far (none: every seated player who is no sinner).
/// </para>
/// </summary>
public sealed class HolyMassEvent
{
    const float SeatReachDy = 2.5f;

    readonly MassConfig _cfg;
    readonly Church _church;
    readonly CitizenCrowd _crowd;
    readonly HashSet<int> _kept = new();                    // the seats the config keeps for players
    readonly Dictionary<ushort, int> _seatOf = new();       // player -> seat
    readonly Dictionary<ushort, float> _seatedFor = new();  // player -> seconds seated while the Mass progressed
    readonly HashSet<ushort> _sinners = new();
    float _phaseLeft;
    float _replaceLeft = -1;   // seconds until a new pastor walks out (<0: none pending)
    float _resumeLeft = -1;    // seconds after the new pastor has arrived until the Mass goes on
    bool _arrived;             // the new pastor stands at his spot
    Citizen? _pastor;

    public HolyMassEvent(MassConfig cfg, Church church, CitizenCrowd crowd)
    {
        _cfg = cfg; _church = church; _crowd = crowd;
        crowd.Killed += OnKilled;
    }

    public MassPhase Phase { get; private set; }
    public bool Running => Phase != MassPhase.Idle;
    /// <summary>How often the pastor has been killed this event.</summary>
    public int PastorKills { get; private set; }
    /// <summary>Seconds of Mass progress so far.</summary>
    public float Progress { get; private set; }
    /// <summary>The pastor stands at his spot and the pause after a kill is over.</summary>
    public bool PastorReady { get; private set; }
    /// <summary>The chant plays and the Mass progresses.</summary>
    public bool Chanting => Phase == MassPhase.Mass && PastorReady;
    public bool IsSinner(ushort player) => _sinners.Contains(player);
    public float SeatedSeconds(ushort player) => _seatedFor.GetValueOrDefault(player);
    public int? SeatOf(ushort player) => _seatOf.TryGetValue(player, out var s) ? s : null;
    public int SeatedPlayers => _seatOf.Count;
    /// <summary>The seats kept for players by the config.</summary>
    public IReadOnlyCollection<int> KeptSeats => _kept;
    /// <summary>The pastor now in the world (null between his death and the new one).</summary>
    public Citizen? Pastor => _pastor;
    public ChurchSpot? PastorSpot => _church.Pastor;

    /// <summary>The phase changed (also to Idle at the end). The church doors follow it.</summary>
    public event Action<MassPhase>? MassPhaseChanged;
    /// <summary>The pastor was killed for the last time: he turns into the demon. The Mass is void. The event stays until it is ended.</summary>
    public event Action? PastorTurns;
    /// <summary>The Mass completed: the players who are blessed (the server teleports and tells them).</summary>
    public event Action<IReadOnlyList<ushort>>? Completed;
    /// <summary>Something the clients show changed: the chant on or off, the pastor's kills, the phase.</summary>
    public event Action? Changed;
    /// <summary>A player killed a citizen, monk or pastor during the event (the killer is now a sinner).</summary>
    public event Action<CitizenKilled>? Sinned;

    /// <summary>Why the event cannot start with this church, or null.</summary>
    public string? WhyNot() =>
        Running ? "The Holy Mass is already on. /event current end ends it." :
        _church.Pastor == null ? "church.json has no pastor spot: export the map again." : null;

    /// <summary>Starts the gather: citizens leave for every seat but the kept ones, the pastor and the monks take their places. Returns how many citizens were sent.</summary>
    public int Start()
    {
        if (WhyNot() != null) return 0;
        _kept.Clear(); _seatOf.Clear(); _seatedFor.Clear(); _sinners.Clear();
        PastorKills = 0; Progress = 0; PastorReady = false; _replaceLeft = _resumeLeft = -1; _arrived = false;
        foreach (var id in KeepSeatsFor()) if (_crowd.Reserve(id)) _kept.Add(id);
        var sent = _crowd.Spawn(_crowd.FreeSeats.ToList(), _cfg.StaggerSeconds, _cfg.HouseRadiusM);
        _pastor = _crowd.SpawnRole(CitizenRole.Pastor, new[] { _church.Pastor!.Pos }, _church.Pastor.Yaw);
        foreach (var m in _church.Monks) _crowd.SpawnRole(CitizenRole.Monk, new[] { m.Pos }, m.Yaw);
        PastorReady = _pastor != null;
        if (_pastor == null) _replaceLeft = 1; // the entity cap: try again
        _phaseLeft = _cfg.GatherSeconds;
        SetPhase(MassPhase.Gather);
        return sent;
    }

    /// <summary>The seats to keep for players: ground-floor pews spread along the id order.</summary>
    List<int> KeepSeatsFor()
    {
        var pews = _church.Seats.Where(s => !s.Stand && s.Floor == 0).Select(s => s.Id).OrderBy(i => i).ToList();
        var n = Math.Min(_cfg.PlayerSeats, pews.Count);
        var picked = new List<int>();
        for (var i = 0; i < n; i++) picked.Add(pews[(int)((i + 0.5) * pews.Count / n)]);
        return picked;
    }

    public void Update(float dt)
    {
        if (Phase == MassPhase.Idle || dt <= 0) return;
        if (Phase is MassPhase.Gather or MassPhase.Mass) TickPastor(dt);
        switch (Phase)
        {
            case MassPhase.Gather:
                _phaseLeft -= dt;
                if (_phaseLeft <= 0) { _phaseLeft = 0; SetPhase(MassPhase.Mass); }
                break;
            case MassPhase.Mass:
                if (!PastorReady) break;
                Progress += dt;
                foreach (var p in _seatOf.Keys.ToList()) _seatedFor[p] = _seatedFor.GetValueOrDefault(p) + dt;
                if (Progress >= _cfg.MassSeconds) Complete();
                break;
            case MassPhase.Completed:
                _phaseLeft -= dt;
                if (_phaseLeft <= 0) End();
                break;
        }
    }

    /// <summary>A killed pastor is replaced after a pause: a new one walks out of the sacristy and the Mass resumes some seconds after he has arrived.</summary>
    void TickPastor(float dt)
    {
        if (_replaceLeft >= 0)
        {
            _replaceLeft -= dt;
            if (_replaceLeft > 0) return;
            var spot = _church.Pastor!;
            var from = _church.SacristyDoor?.Pos ?? spot.Pos;
            _pastor = _crowd.SpawnRole(CitizenRole.Pastor, new[] { from, spot.Pos }, spot.Yaw);
            if (_pastor == null) { _replaceLeft = 1; return; }
            _replaceLeft = -1; _arrived = false; _resumeLeft = _cfg.ResumeSeconds;
            Changed?.Invoke();
            return;
        }
        if (PastorReady || _pastor is not { Alive: true } p) return;
        if (!_arrived && p.State is CitizenState.Standing or CitizenState.Clapping) _arrived = true;
        if (!_arrived) return;
        _resumeLeft -= dt;
        if (_resumeLeft <= 0) { PastorReady = true; Changed?.Invoke(); }
    }

    void OnKilled(CitizenKilled k)
    {
        if (!Running) return;
        if (k.Killer != 0) { _sinners.Add(k.Killer); Sinned?.Invoke(k); }
        if (k.Role != CitizenRole.Pastor) return;
        PastorKills++;
        PastorReady = false; _arrived = false;
        if (Phase is not (MassPhase.Gather or MassPhase.Mass)) { Changed?.Invoke(); return; }
        if (PastorKills >= _cfg.PastorKillsToTurn)
        {
            _replaceLeft = -1;
            SetPhase(MassPhase.Void);
            PastorTurns?.Invoke();
            return;
        }
        _replaceLeft = _cfg.PastorReplaceSeconds;
        Changed?.Invoke();
    }

    /// <summary>Admin test (<c>/event demon</c>): the pastor turns now, without the kills. The Mass is void; from an idle event it starts one that is void at once.</summary>
    public void ForceTurn()
    {
        if (Phase == MassPhase.Void) return;
        PastorKills = Math.Max(PastorKills, _cfg.PastorKillsToTurn);
        PastorReady = false; _replaceLeft = _resumeLeft = -1;
        SetPhase(MassPhase.Void);
        PastorTurns?.Invoke();
    }

    // ---- players ----

    /// <summary>The seat a player at <paramref name="pos"/> could sit in now: the nearest one in reach that no citizen has and no player sits in. Only while the Mass is gathering or on.</summary>
    public int? SeatNear(Vector3 pos)
    {
        if (Phase is not (MassPhase.Gather or MassPhase.Mass)) return null;
        int? best = null; var bd = float.MaxValue;
        foreach (var s in _church.Seats)
        {
            if (s.Stand || _seatOf.ContainsValue(s.Id) || !(_kept.Contains(s.Id) || _crowd.IsSeatFree(s.Id))) continue;
            var feet = Church.FeetOf(s, 0);
            var dx = feet.X - pos.X; var dz = feet.Z - pos.Z;
            var d = MathF.Sqrt(dx * dx + dz * dz);
            if (d > _cfg.SitReachM || MathF.Abs(feet.Y - pos.Y) > SeatReachDy || d >= bd) continue;
            best = s.Id; bd = d;
        }
        return best;
    }

    /// <summary>Sits the player in the nearest free seat in reach. The seat is theirs until they stand; no citizen is sent to it.</summary>
    public ChurchSeat? TrySit(ushort player, Vector3 pos)
    {
        if (_seatOf.ContainsKey(player) || SeatNear(pos) is not { } id) return null;
        if (!_kept.Contains(id) && !_crowd.Reserve(id)) return null;
        _seatOf[player] = id;
        return _church.Seat(id);
    }

    /// <summary>The player stands up (or died, or left). A seat the config keeps stays kept; any other is free again.</summary>
    public void Stand(ushort player)
    {
        if (!_seatOf.Remove(player, out var id)) return;
        if (!_kept.Contains(id)) _crowd.Unreserve(id);
    }

    // ---- the end ----

    /// <summary>"/event current end": concludes the event as if it was completed (the Mass done, the blessed chosen); a Mass already done or void is cleaned up at once.</summary>
    public void Conclude()
    {
        switch (Phase)
        {
            case MassPhase.Gather or MassPhase.Mass: Complete(); break;
            case MassPhase.Completed or MassPhase.Void: End(); break;
        }
    }

    void Complete()
    {
        var blessed = _seatOf.Keys.Where(p => IsBlessed(true, _sinners.Contains(p), SeatedSeconds(p), Progress, _cfg.BlessedShare)).OrderBy(p => p).ToList();
        _crowd.Finish(_cfg.ClapShare);
        foreach (var p in _seatOf.Keys.ToList()) Stand(p);
        PastorReady = false;
        _phaseLeft = _cfg.CompletedSeconds;
        SetPhase(MassPhase.Completed);
        Completed?.Invoke(blessed);
    }

    /// <summary>The blessing rule: seated at the end, seated for the share of the Mass, not a sinner. No Mass time yet (ended early): the share is met.</summary>
    public static bool IsBlessed(bool seatedAtEnd, bool sinner, float seatedSeconds, float massSeconds, float share) =>
        seatedAtEnd && !sinner && (massSeconds <= 0 || seatedSeconds >= share * massSeconds - 1e-3f);

    /// <summary>Everyone is removed (citizens, pastor, monks) and the event is over.</summary>
    public void End()
    {
        if (Phase == MassPhase.Idle) return;
        _crowd.Clear();
        _seatOf.Clear(); _kept.Clear();
        _pastor = null; PastorReady = false; _replaceLeft = _resumeLeft = -1;
        SetPhase(MassPhase.Idle);
    }

    void SetPhase(MassPhase phase)
    {
        if (Phase == phase) return;
        Phase = phase;
        MassPhaseChanged?.Invoke(phase);
        Changed?.Invoke();
    }
}

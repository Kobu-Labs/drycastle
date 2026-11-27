using Godot;
using Drycastle.Shooter.Net.Server.Cartel;
using Drycastle.Shooter.Net.Server.Highs;
using Drycastle.Shooter.Net.Server.Parish;
using Drycastle.Shooter.Net.Server.Seating;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Plantation;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Houses;

/// <summary>
/// The keeper of the Drills on the dressed Trap houses' safes (#496, GLOSSARY "Drill", docs/cartel.md "Trap house"; trap_house.json <c>drill</c>). The machine and its rules are
/// ShooterCore's (<see cref="Drill"/>, <see cref="DrillRules"/>); this class feeds them the players. A Job arms a dressed house's safe for its party at a tier (<see cref="Arm"/>); a party
/// player holds the interact key at the safe's stand spot to set the drill and, when it jams, to fix it (<see cref="InteractHold"/>, counted from the inputs; nothing else in reach of the
/// key: <see cref="KeyCompetition"/>). Through, the safe opens, its cash Duffels lie in front of it for the party (<see cref="DuffelBook.Place"/>) and <see cref="Done"/> is raised.
/// <para>
/// The players near a safe (<c>near_m</c>) are told its Drill (<see cref="EventKind.DrillState"/> on every change and when they come near, <see cref="EventKind.DrillCleared"/> when it
/// goes or they go far); a party player is told what their key's hold would do (<see cref="EventKind.DrillReach"/>). A house that is stripped loses its Drill; a new round, every one.
/// </para>
/// <para>Seam for the Trap house heist: <see cref="Arm"/>, <see cref="Disarm"/>, <see cref="Of"/> and <see cref="Done"/>. The admin's <c>/drill [tier|off]</c> arms the nearest dressed safe for the admin.</para>
/// </summary>
internal sealed class DrillServer
{
    /// <summary>Hysteresis of the nearness: a player told of a Drill is cleared only past this times <c>near_m</c>.</summary>
    const float FarFactor = 1.2f;

    readonly ServerContext _ctx;
    readonly TrapHouseServer _houses;
    readonly DuffelServer _duffels;
    readonly KeyCompetition _competition;
    readonly Sitters _sitters;
    readonly PounceServer _pounce;
    readonly StreakServer _streaks;
    readonly Random _rng = new();
    readonly Dictionary<string, Drill> _drills = new();
    /// <summary>Who is told of each Drill (the players near its safe).</summary>
    readonly Dictionary<string, HashSet<ushort>> _told = new();
    readonly Dictionary<ushort, InteractHold> _holds = new();
    /// <summary>What each human was last told their key's hold would do (absent: nothing).</summary>
    readonly Dictionary<ushort, (DrillHold Hold, string House)> _reachSent = new();
    /// <summary>The Drill each human's key would work now, worked out once a tick.</summary>
    readonly Dictionary<ushort, Drill> _reach = new();

    public DrillServer(ServerContext ctx, TrapHouseServer houses, DuffelServer duffels, KeyCompetition competition, Sitters sitters, PounceServer pounce, StreakServer streaks)
    {
        _ctx = ctx; _houses = houses; _duffels = duffels; _competition = competition; _sitters = sitters; _pounce = pounce; _streaks = streaks;
    }

    /// <summary>A Drill went through: its house, the Drill, and the cash Duffels that lie by the open safe (the party's).</summary>
    public event Action<Drill, IReadOnlyList<Duffel>>? Done;

    /// <summary>The Drills by building id.</summary>
    public IReadOnlyDictionary<string, Drill> Drills => _drills;

    /// <summary>The Drill on house <paramref name="house"/>'s safe, or null.</summary>
    public Drill? Of(string house) => _drills.GetValueOrDefault(house);

    TrapHouseFile? Data => _houses.Data;

    /// <summary>
    /// Arms the safe of dressed house <paramref name="house"/> for <paramref name="party"/> (read live: the Job's set) at <paramref name="tier"/>: a new Drill waits there to be set (a Drill
    /// already on it is replaced). Null when the house is not dressed or has no safe.
    /// </summary>
    public Drill? Arm(string house, IReadOnlySet<ushort> party, int tier)
    {
        if (Data is not { } data || !_houses.Dressed.TryGetValue(house, out var d) || d.Safe == null) return null;
        Disarm(house);
        var run = DrillRules.RunSeconds(data.Drill, tier);
        var drill = new Drill(house, tier, party, run, DrillRules.JamsAt(data.Drill, run, _rng));
        _drills[house] = drill;
        _told[house] = new HashSet<ushort>();
        GD.Print($"server: {drill} armed ({drill.Jams} jam(s) at {string.Join(", ", drill.JamsAt.Select(s => s.ToString("0")))} s)");
        return drill;
    }

    /// <summary>House <paramref name="house"/>'s Drill goes (the Duffels it paid out stay: the Job removes them). False when it had none.</summary>
    public bool Disarm(string house)
    {
        if (!_drills.Remove(house)) return false;
        if (_told.Remove(house, out var told))
            foreach (var id in told) if (_ctx.Players.TryGetValue(id, out var c)) _ctx.SendEvent(c, EventKind.DrillCleared, DrillCodec.EncodeCleared(house));
        return true;
    }

    /// <summary>A new round: no Job outlives it, so no Drill does.</summary>
    public void RoundStart()
    {
        foreach (var house in _drills.Keys.ToList()) Disarm(house);
        _holds.Clear(); _reach.Clear();
        foreach (var (id, _) in _reachSent) if (_ctx.Players.TryGetValue(id, out var c)) _ctx.SendEvent(c, EventKind.DrillReach, DrillCodec.EncodeReach(DrillHold.None, ""));
        _reachSent.Clear();
    }

    /// <summary>
    /// Every input of a player, with the interact key's state (beside the plantation's): held for the hold's seconds at a safe whose Drill waits to be set or fixed, by a party player on foot
    /// and alive with nothing else in reach of the key, sets or fixes it (once per hold).
    /// </summary>
    public void Use(ServerClient c, bool useDown)
    {
        if (_drills.Count == 0 || c.IsBot || Data is not { } data) return;
        var drill = _reach.GetValueOrDefault(c.Id);
        var hold = drill?.Hold ?? DrillHold.None;
        var h = _holds.GetValueOrDefault(c.Id);
        var done = h.Tick(useDown, hold != DrillHold.None, 1.0 / Wire.TickRate, DrillRules.HoldSeconds(data.Drill, hold));
        _holds[c.Id] = h;
        if (!done || drill == null || !drill.Work()) return;
        GD.Print($"server: {c.Name} #{c.Id} {(hold == DrillHold.Set ? "set" : "fixed")} the {drill}");
        TellNear(drill);
    }

    bool AliveOnFoot(ServerClient c) =>
        _ctx.Match.Get(c.Id)?.Life == PlayerLife.Alive && c.Car == null && !_sitters.IsSeated(c.Id) && !_pounce.IsPinned(c.Id) && _ctx.Tick > c.HeldUntilTick && _streaks.Rules.ControlOf(c.Id) == null;

    /// <summary>Every tick: a Drill whose house was stripped goes; the Drills run (a jam, the open safe and its cash); the near players are told; each human's reach is worked out.</summary>
    public void Tick(float dt)
    {
        if (_drills.Count == 0 && _reachSent.Count == 0) return;
        foreach (var house in _drills.Keys.Where(h => !_houses.Dressed.ContainsKey(h)).ToList()) Disarm(house);
        foreach (var drill in _drills.Values.ToList())
        {
            var step = drill.Tick(dt);
            if (step == DrillStep.None) continue;
            GD.Print($"server: {drill}: {step}");
            TellNear(drill);
            if (step == DrillStep.Done) PayOut(drill);
        }
        Nearness();
        Reach();
    }

    /// <summary>The open safe's cash Duffels, for the Drill's party, and <see cref="Done"/>.</summary>
    void PayOut(Drill drill)
    {
        if (Data is not { } data || !_houses.Dressed.TryGetValue(drill.House, out var d) || d.Safe is not { } safe) return;
        var depth = data.Props[data.KindOf(data.Safe.Prop)].Depth;
        var paid = new List<Duffel>();
        foreach (var at in DrillRules.CashSpots(data.Drill, safe, depth, DrillRules.CashDuffels(data.Drill, drill.Tier)))
            if (_duffels.Book.Place(DuffelKind.Cash, drill.Party, at, safe.Yaw) is { } duffel) paid.Add(duffel);
        GD.Print($"server: the safe of {drill.House} is open: {paid.Count} cash duffel(s)");
        Done?.Invoke(drill, paid);
    }

    DrillMsg Msg(Drill d, ushort to) => new(d.House, d.State, d.Party.Contains(to), d.Progress, d.RunSeconds);

    /// <summary>Every player told of the Drill hears its change.</summary>
    void TellNear(Drill d)
    {
        if (!_told.TryGetValue(d.House, out var told)) return;
        foreach (var id in told) if (_ctx.Players.TryGetValue(id, out var c)) _ctx.SendEvent(c, EventKind.DrillState, DrillCodec.EncodeState(Msg(d, id)));
    }

    /// <summary>A human who came near a safe is told its Drill; one who went far, or left, is cleared.</summary>
    void Nearness()
    {
        if (Data is not { } data) return;
        var near = data.Drill.NearM;
        foreach (var (house, drill) in _drills)
        {
            if (!_houses.Dressed.TryGetValue(house, out var d) || d.Safe is not { } safe || !_told.TryGetValue(house, out var told)) continue;
            told.RemoveWhere(id => !_ctx.Players.ContainsKey(id));
            foreach (var c in _ctx.Roster)
            {
                if (c.IsBot || !c.Joined) continue;
                var dist = NVec3.Distance(c.Body.GlobalPosition.ToNumerics(), safe.Pos);
                if (!told.Contains(c.Id) && dist <= near) { told.Add(c.Id); _ctx.SendEvent(c, EventKind.DrillState, DrillCodec.EncodeState(Msg(drill, c.Id))); }
                else if (told.Contains(c.Id) && dist > near * FarFactor) { told.Remove(c.Id); _ctx.SendEvent(c, EventKind.DrillCleared, DrillCodec.EncodeCleared(house)); }
            }
        }
    }

    /// <summary>Which Drill each human's key would work now (the nearest stand spot in reach whose hold counts); a change is sent to them.</summary>
    void Reach()
    {
        _reach.Clear();
        if (Data is { } data && _drills.Count > 0)
            foreach (var c in _ctx.Roster)
            {
                if (c.IsBot || !c.Joined || !_drills.Values.Any(x => x.Party.Contains(c.Id))) continue;
                var feet = c.Body.GlobalPosition.ToNumerics();
                var alive = AliveOnFoot(c);
                KeyCompetitor? others = null;
                foreach (var (house, drill) in _drills)
                {
                    if (!_houses.Dressed.TryGetValue(house, out var d) || d.Safe is not { } safe) continue;
                    var inReach = DrillRules.InReach(data.Drill, safe.Stand, feet);
                    if (inReach && drill.Party.Contains(c.Id) && drill.Hold != DrillHold.None) others ??= _competition.Of(c);
                    if (DrillRules.HoldOf(drill, c.Id, alive, inReach, others ?? KeyCompetitor.None) != DrillHold.None) { _reach[c.Id] = drill; break; }
                }
            }
        foreach (var c in _ctx.Roster)
        {
            if (c.IsBot || !c.Joined) continue;
            var now = _reach.TryGetValue(c.Id, out var dr) ? (dr.Hold, dr.House) : (DrillHold.None, "");
            var was = _reachSent.GetValueOrDefault(c.Id, (DrillHold.None, ""));
            if (now == was) continue;
            if (now.Item1 == DrillHold.None) _reachSent.Remove(c.Id); else _reachSent[c.Id] = now;
            _ctx.SendEvent(c, EventKind.DrillReach, DrillCodec.EncodeReach(now.Item1, now.Item2));
        }
        foreach (var gone in _reachSent.Keys.Where(id => !_ctx.Players.ContainsKey(id)).ToList()) _reachSent.Remove(gone);
        foreach (var gone in _holds.Keys.Where(id => !_ctx.Players.ContainsKey(id)).ToList()) _holds.Remove(gone);
    }

    /// <summary>"/drill [tier|off]" (TEST): arms the safe of the dressed Trap house nearest the admin for the admin (a party of one) at the tier (1 by default); off disarms every one.</summary>
    public void Chat(ServerClient c, IReadOnlyList<string> args)
    {
        if (args.Count > 0 && args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            var n = _drills.Count;
            foreach (var house in _drills.Keys.ToList()) Disarm(house);
            _ctx.ChatTo(c, ChatLineKind.Info, n == 0 ? "No safe is armed." : $"Disarmed {n} drill{(n == 1 ? "" : "s")}.");
            return;
        }
        var tier = 1;
        if (args.Count > 0 && !int.TryParse(args[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out tier)) { _ctx.ChatTo(c, ChatLineKind.Error, "Say the tier: /drill [tier|off]."); return; }
        var at = c.Body.GlobalPosition.ToNumerics();
        var nearest = _houses.Dressed.Values.Where(d => d.Safe != null).MinBy(d => NVec3.Distance(d.Safe!.Pos, at));
        if (nearest == null) { _ctx.ChatTo(c, ChatLineKind.Error, "No dressed Trap house has a safe: /traphouse first."); return; }
        if (Arm(nearest.House, new HashSet<ushort> { c.Id }, tier) is not { } drill) { _ctx.ChatTo(c, ChatLineKind.Error, "That safe cannot be armed."); return; }
        _ctx.ChatTo(c, ChatLineKind.Info, $"The safe of {drill.House} ({nearest.Safe!.Room}, {NVec3.Distance(nearest.Safe.Pos, at):0} m away) is armed for you, tier {tier}: {drill.RunSeconds:0} s, {drill.Jams} jam(s). Hold E at it to set the drill.");
    }
}

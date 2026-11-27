using Godot;
using Drycastle.Shooter.Net.Server.Highs;
using Drycastle.Shooter.Net.Server.Parish;
using Drycastle.Shooter.Net.Server.Seating;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Plantation;

namespace Drycastle.Shooter.Net.Server.Houses;

/// <summary>
/// The interact key at the safes (<see cref="DrillServer"/>): which Drill each human's hold would work now (a party player, alive and on foot, at a stand spot, nothing else in reach that
/// wins the key: <see cref="KeyCompetition"/>, <see cref="DrillRules.HoldOf"/>), told to them on a change (<see cref="EventKind.DrillReach"/>), and their holds, counted from the inputs
/// (<see cref="InteractHold"/>). A player who left is forgotten on the next tick.
/// </summary>
internal sealed class DrillKey
{
    readonly ServerContext _ctx;
    readonly KeyCompetition _competition;
    readonly Sitters _sitters;
    readonly PounceServer _pounce;
    readonly StreakServer _streaks;
    readonly Dictionary<ushort, InteractHold> _holds = new();
    /// <summary>What each human was last told their key's hold would do (absent: nothing).</summary>
    readonly Dictionary<ushort, (DrillHold Hold, string House)> _sent = new();
    /// <summary>The Drill each human's key would work now, worked out once a tick.</summary>
    readonly Dictionary<ushort, Drill> _reach = new();

    public DrillKey(ServerContext ctx, KeyCompetition competition, Sitters sitters, PounceServer pounce, StreakServer streaks)
    {
        _ctx = ctx; _competition = competition; _sitters = sitters; _pounce = pounce; _streaks = streaks;
    }

    /// <summary>Someone was told their key would work a safe (so a tick without Drills still has a None to send).</summary>
    public bool AnyTold => _sent.Count > 0;

    /// <summary>A player's input: the key held for the hold's seconds at the Drill in reach sets or fixes it (once per hold). The Drill worked, or null.</summary>
    public Drill? Use(ServerClient c, bool useDown, TrapDrillData data)
    {
        var drill = _reach.GetValueOrDefault(c.Id);
        var hold = drill?.Hold ?? DrillHold.None;
        var h = _holds.GetValueOrDefault(c.Id);
        var done = h.Tick(useDown, hold != DrillHold.None, 1.0 / Wire.TickRate, DrillRules.HoldSeconds(data, hold));
        _holds[c.Id] = h;
        if (!done || drill == null || !drill.Work()) return null;
        GD.Print($"server: {c.Name} #{c.Id} {(hold == DrillHold.Set ? "set" : "fixed")} the {drill}");
        return drill;
    }

    bool AliveOnFoot(ServerClient c) =>
        _ctx.Match.Get(c.Id)?.Life == PlayerLife.Alive && c.Car == null && !_sitters.IsSeated(c.Id) && !_pounce.IsPinned(c.Id) && _ctx.Tick > c.HeldUntilTick && _streaks.Rules.ControlOf(c.Id) == null;

    static bool InAnyParty(Dictionary<string, Drill> drills, ushort id)
    {
        foreach (var d in drills.Values) if (d.Party.Contains(id)) return true;
        return false;
    }

    /// <summary>Every tick: which Drill each human's key would work now (the first stand spot in reach whose hold counts); a change is sent to them.</summary>
    public void Tick(Dictionary<string, Drill> drills, IReadOnlyDictionary<string, TrapHouseDressing> dressed, TrapDrillData? data)
    {
        _reach.Clear();
        if (data != null && drills.Count > 0)
            foreach (var c in _ctx.Roster)
            {
                if (c.IsBot || !c.Joined || !InAnyParty(drills, c.Id)) continue;
                var feet = c.Body.GlobalPosition.ToNumerics();
                var alive = AliveOnFoot(c);
                KeyCompetitor? others = null;
                foreach (var (house, drill) in drills)
                {
                    if (!dressed.TryGetValue(house, out var d) || d.Safe is not { } safe) continue;
                    var inReach = DrillRules.InReach(data, safe.Stand, feet);
                    if (inReach && drill.Party.Contains(c.Id) && drill.Hold != DrillHold.None) others ??= _competition.Of(c);
                    if (DrillRules.HoldOf(drill, c.Id, alive, inReach, others ?? KeyCompetitor.None) != DrillHold.None) { _reach[c.Id] = drill; break; }
                }
            }
        foreach (var c in _ctx.Roster)
        {
            if (c.IsBot || !c.Joined) continue;
            var now = _reach.TryGetValue(c.Id, out var dr) ? (dr.Hold, dr.House) : (DrillHold.None, "");
            var was = _sent.GetValueOrDefault(c.Id, (DrillHold.None, ""));
            if (now == was) continue;
            if (now.Item1 == DrillHold.None) _sent.Remove(c.Id); else _sent[c.Id] = now;
            _ctx.SendEvent(c, EventKind.DrillReach, DrillCodec.EncodeReach(now.Item1, now.Item2));
        }
        // the players who left (a removal does not end the walk)
        foreach (var id in _sent.Keys) if (!_ctx.Players.ContainsKey(id)) _sent.Remove(id);
        foreach (var id in _holds.Keys) if (!_ctx.Players.ContainsKey(id)) _holds.Remove(id);
    }

    /// <summary>A new round: no hold, no reach; whoever was told is told nothing again.</summary>
    public void Clear()
    {
        _holds.Clear(); _reach.Clear();
        foreach (var id in _sent.Keys) if (_ctx.Players.TryGetValue(id, out var c)) _ctx.SendEvent(c, EventKind.DrillReach, DrillCodec.EncodeReach(DrillHold.None, ""));
        _sent.Clear();
    }
}

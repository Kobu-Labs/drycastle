using Godot;
using Drycastle.Shooter.Net.Server.Statuses;
using Drycastle.ShooterCore.Demon;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Server.Parish;

/// <summary>
/// The demon's pounce on the server (<see cref="DemonBrain"/>, DemonPounced events): the victim is marked at the wind (event 223), and on the landing the server holds their body like a seat
/// (<see cref="IsPinned"/>: no movement, no weapon, nothing touches them, <see cref="MatchSim.SetHeld"/>) until the pin is over; then their health is set to the data's <c>leave_hp</c>, they get a
/// second of immunity to the demon's fire (<see cref="Immune"/>) and it leaps away. Every other end (they leave, the demon dies, the event is cleared) lets them go unchanged,
/// and <see cref="Sweep"/> lets go of anyone nothing holds, every tick.
/// </summary>
internal sealed class PounceServer
{
    readonly ServerContext _ctx;
    readonly Func<DemonFile> _demon;
    readonly StatusServer _status;
    /// <summary>Players held down by the pounce, and the yaw they lie at (their head towards (sin yaw, cos yaw)).</summary>
    readonly Dictionary<ushort, float> _pins = new();
    /// <summary>Until when (server seconds) a released victim is immune to the demon's fire and flame.</summary>
    readonly Dictionary<ushort, double> _immune = new();

    /// <param name="demon">holy_mass_demon.json, read live (the host may replace it).</param>
    public PounceServer(ServerContext ctx, Func<DemonFile> demon, StatusServer status) { _ctx = ctx; _demon = demon; _status = status; }

    /// <summary>The player is held down under the demon: not a target, no input.</summary>
    public bool IsPinned(ushort id) => _pins.ContainsKey(id);

    /// <summary>The yaw a pinned player lies at.</summary>
    public bool TryYaw(ushort id, out float yaw) => _pins.TryGetValue(id, out yaw);

    /// <summary>Just let go of the pounce: the demon's fire does not touch them yet.</summary>
    public bool Immune(ushort id) => _immune.TryGetValue(id, out var until) && _ctx.Now < until;

    /// <summary>One stage of a pounce, as the brain reports it.</summary>
    public void Stage(DemonPounced p)
    {
        switch (p.Stage)
        {
            case PounceStage.Wind:
                _ctx.Broadcast(EventKind.DemonPounce, DemonCodec.EncodePounce(new DemonPounceMsg(p.Victim, PounceStage.Wind)));
                GD.Print($"server: demon: pounces on #{p.Victim}");
                break;
            case PounceStage.Pinned:
                Hold(p);
                break;
            case PounceStage.Released:
                LetGo(p.Victim, true);
                break;
            default:
                LetGo(p.Victim, false);
                break;
        }
    }

    /// <summary>The landing: the victim goes down flat on the floor where they were, the body is held, nothing touches them.</summary>
    void Hold(DemonPounced p)
    {
        if (!_ctx.Players.TryGetValue(p.Victim, out var c)) { LetGo(p.Victim, false); return; }
        c.Body.Teleport(new Vector3(p.Feet.X, p.Feet.Y + 0.02f, p.Feet.Z));
        c.Body.Yaw = p.Yaw; c.Body.Pitch = 0;
        c.Inputs.Clear();
        _ctx.Match.SetPosition(c.Id, p.Feet);
        _ctx.Match.SetHeld(c.Id, true);
        _status.Burns.Extinguish(c.Id);
        _pins[c.Id] = p.Yaw;
        _ctx.Broadcast(EventKind.DemonPounce, DemonCodec.EncodePounce(new DemonPounceMsg(c.Id, PounceStage.Pinned, p.Yaw)));
        GD.Print($"server: demon: #{c.Id} is pinned");
    }

    /// <summary>
    /// The pin is over. <paramref name="release"/> is the demon's release (its time is up): the health is set to <c>pounce.leave_hp</c>, whatever it was, and the victim is immune to the demon's
    /// fire for a moment. Otherwise (they left, the demon died, the event ended) the health is untouched. Either way the hold and the mark are gone and everyone is told.
    /// </summary>
    public void LetGo(ushort id, bool release)
    {
        var demon = _demon();
        _pins.Remove(id);
        _ctx.Match.SetHeld(id, false);
        if (release && _ctx.Players.TryGetValue(id, out var c))
        {
            var hp = demon.Pounce.LeaveHp;
            if (_ctx.Match.SetHealth(id, hp)) c.Body.Health = hp; // the snapshot carries it: the HUD shows it
            _immune[id] = _ctx.Now + demon.Pounce.ImmunityS;
        }
        _ctx.Broadcast(EventKind.DemonPounce, DemonCodec.EncodePounce(new DemonPounceMsg(id, release ? PounceStage.Released : PounceStage.Aborted)));
        GD.Print($"server: demon: #{id} is let go ({(release ? "released at " + demon.Pounce.LeaveHp + " hp" : "aborted")})");
    }

    /// <summary>Everyone held is let go unchanged (the event is cleared).</summary>
    public void LetGoAll()
    {
        foreach (var id in _pins.Keys.ToList()) LetGo(id, false);
    }

    /// <summary>Every tick, whatever happened: lets go of anyone held that the brain is not pinning (no fight, the demon gone or dead, another victim) or who is gone or dead. No path leaves a player held.</summary>
    public void Sweep(DemonBrain? fightingBrain)
    {
        if (_immune.Count > 0)
            foreach (var id in _immune.Where(kv => kv.Value <= _ctx.Now).Select(kv => kv.Key).ToList()) _immune.Remove(id);
        if (_pins.Count == 0) return;
        foreach (var id in PounceRules.Stuck(_pins.Keys, fightingBrain, i => _ctx.Players.ContainsKey(i) && _ctx.IsAlive(i))) LetGo(id, false);
    }

    /// <summary>Join: the pounce as it is, for a client that arrives during one (the victim's mark or the pin).</summary>
    public void SendOnJoin(ServerClient c, DemonBrain? fightingBrain)
    {
        if (fightingBrain is not { PounceVictim: { } v }) return;
        var pinned = _pins.TryGetValue(v, out var yaw);
        _ctx.SendEvent(c, EventKind.DemonPounce, DemonCodec.EncodePounce(new DemonPounceMsg(v, pinned ? PounceStage.Pinned : PounceStage.Wind, pinned ? yaw : 0)));
    }
}

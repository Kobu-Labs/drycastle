using Godot;
using Drycastle.Shooter.Net.Server.Cartel;
using Drycastle.Shooter.Net.Server.Highs;
using Drycastle.Shooter.Net.Server.Parish;
using Drycastle.Shooter.Net.Server.Seating;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Houses;

/// <summary>
/// The keeper of the Drills on the dressed Trap houses' safes (#496, GLOSSARY "Drill", docs/cartel.md "Trap house"; trap_house.json <c>drill</c>). The machine and its rules are
/// ShooterCore's (<see cref="Drill"/>, <see cref="DrillRules"/>); this class feeds them the players. A Job arms a dressed house's safe for its party at a tier (<see cref="Arm"/>); a party
/// player holds the interact key at the safe's stand spot to set the drill and, when it jams, to fix it (<see cref="DrillKey"/>: counted from the inputs, nothing else in reach that wins
/// the key). Through, the safe opens, its cash Duffels lie in front of it for the party (<see cref="DuffelBook.Place"/>) and <see cref="Done"/> is raised.
/// <para>
/// The players near a safe are told its Drill (<see cref="DrillAudience"/>); a party player is told what their key's hold would do (<see cref="EventKind.DrillReach"/>). A house that is
/// stripped loses its Drill (<see cref="TrapHouseServer.Stripped"/>); a new round, every one.
/// </para>
/// <para>Seam for the Trap house heist: <see cref="Arm"/>, <see cref="Disarm"/>, <see cref="Of"/> and <see cref="Done"/>. The admin's <c>/drill [tier|off]</c> arms the nearest dressed safe for the admin.</para>
/// </summary>
internal sealed class DrillServer
{
    readonly ServerContext _ctx;
    readonly TrapHouseServer _houses;
    readonly DuffelServer _duffels;
    readonly DrillAudience _audience;
    readonly DrillKey _key;
    readonly Random _rng = new();
    readonly Dictionary<string, Drill> _drills = new();

    public DrillServer(ServerContext ctx, TrapHouseServer houses, DuffelServer duffels, KeyCompetition competition, Sitters sitters, PounceServer pounce, StreakServer streaks)
    {
        _ctx = ctx; _houses = houses; _duffels = duffels;
        _audience = new DrillAudience(ctx);
        _key = new DrillKey(ctx, competition, sitters, pounce, streaks);
        houses.Stripped += house => Disarm(house); // its safe went with its props
    }

    /// <summary>A Drill went through: its house, the Drill, and the cash Duffels that lie by the open safe (the party's).</summary>
    public event Action<Drill, IReadOnlyList<Duffel>>? Done;

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
        _audience.Open(house);
        GD.Print($"server: {drill} armed ({drill.Jams} jam(s) at {string.Join(", ", drill.JamsAt.Select(s => s.ToString("0")))} s)");
        return drill;
    }

    /// <summary>House <paramref name="house"/>'s Drill goes (the Duffels it paid out stay: the Job removes them). False when it had none.</summary>
    public bool Disarm(string house)
    {
        if (!_drills.Remove(house)) return false;
        _audience.Close(house);
        return true;
    }

    /// <summary>A new round: no Job outlives it, so no Drill does.</summary>
    public void RoundStart()
    {
        foreach (var house in _drills.Keys) Disarm(house); // a removal does not end the walk
        _key.Clear();
    }

    /// <summary>Every input of a player, with the interact key's state (beside the plantation's): a hold at a safe whose Drill waits sets or fixes it (<see cref="DrillKey.Use"/>).</summary>
    public void Use(ServerClient c, bool useDown)
    {
        if (_drills.Count == 0 || c.IsBot || Data is not { } data) return;
        if (_key.Use(c, useDown, data.Drill) is { } drill) _audience.Tell(drill);
    }

    /// <summary>Every tick: the Drills run (a jam, the open safe and its cash); the near players are told; each human's reach is worked out.</summary>
    public void Tick(float dt)
    {
        if (_drills.Count == 0 && !_key.AnyTold) return;
        foreach (var drill in _drills.Values) // the Done handlers only tell the party; a Disarm in one would not end the walk either
        {
            var step = drill.Tick(dt);
            if (step == DrillStep.None) continue;
            GD.Print($"server: {drill}: {step}");
            _audience.Tell(drill);
            if (step == DrillStep.Done) PayOut(drill);
        }
        var data = Data;
        if (data != null) _audience.Tick(_drills, _houses.Dressed, data.Drill.NearM);
        _key.Tick(_drills, _houses.Dressed, data?.Drill);
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

    /// <summary>
    /// "/drill [tier|off]" (TEST): arms the safe of the dressed Trap house nearest the admin for the admin (a party of one) at the tier (1 by default); off disarms every one. A safe a
    /// running Job holds is left alone: its Job would run on with nothing to open.
    /// </summary>
    public void Chat(ServerClient c, IReadOnlyList<string> args)
    {
        if (args.Count > 0 && args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            int n = 0, held = 0;
            foreach (var house in _drills.Keys) // a removal does not end the walk
            {
                if (_houses.Held(house)) { held++; continue; }
                Disarm(house); n++;
            }
            var left = held == 0 ? "" : $" {held} safe{(held == 1 ? " is" : "s are")} a running job's: left armed.";
            _ctx.ChatTo(c, ChatLineKind.Info, (n == 0 ? "No safe disarmed." : $"Disarmed {n} drill{(n == 1 ? "" : "s")}.") + left);
            return;
        }
        var tier = 1;
        if (args.Count > 0 && !int.TryParse(args[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out tier)) { _ctx.ChatTo(c, ChatLineKind.Error, "Say the tier: /drill [tier|off]."); return; }
        var at = c.Body.GlobalPosition.ToNumerics();
        var nearest = _houses.Dressed.Values.Where(d => d.Safe != null).MinBy(d => NVec3.Distance(d.Safe!.Pos, at));
        if (nearest == null) { _ctx.ChatTo(c, ChatLineKind.Error, "No dressed Trap house has a safe: /traphouse first."); return; }
        if (_houses.Held(nearest.House)) { _ctx.ChatTo(c, ChatLineKind.Error, $"The safe of {nearest.House} is a running job's: left as it is."); return; }
        if (Arm(nearest.House, new HashSet<ushort> { c.Id }, tier) is not { } drill) { _ctx.ChatTo(c, ChatLineKind.Error, "That safe cannot be armed."); return; }
        _ctx.ChatTo(c, ChatLineKind.Info, $"The safe of {drill.House} ({nearest.Safe!.Room}, {NVec3.Distance(nearest.Safe.Pos, at):0} m away) is armed for you, tier {tier}: {drill.RunSeconds:0} s, {drill.Jams} jam(s). Hold {{interact}} at it to set the drill.");
    }
}

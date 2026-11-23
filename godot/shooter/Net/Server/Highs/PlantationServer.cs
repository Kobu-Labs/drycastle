using Godot;
using Drycastle.Shooter.Net.Server.Parish;
using Drycastle.Shooter.Net.Server.Seating;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.Shooter.Net.Server.Terrain;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Plantation;
using Drycastle.ShooterCore.Status;

namespace Drycastle.Shooter.Net.Server.Highs;

/// <summary>
/// The marijuana plantation on the server (GLOSSARY "Plantation", "High"; plantation.json). A human player on foot and alive inside a <c>plantation</c> zone (the polygons of zone_polygons.json,
/// <see cref="ZonePolygons"/>) who holds the interact key for <see cref="PlantationFile.HoldSeconds"/>, with nothing else in reach of the key, gets high for <see cref="PlantationFile.HighSeconds"/>
/// (<see cref="PlayerHigh"/>, <c>Player.High</c>). The hold is the rozhlas set's <see cref="InteractHold"/>, counted here from the inputs; the key must be released between holds, and another completed hold
/// while high starts the time over. The plantation has the lowest priority of the key (<see cref="PlantationRules.HoldCounts"/>, <see cref="KeyCompetition"/>). The high is the replicated bit
/// <see cref="StatusKind.High"/> of <see cref="ServerClient.Status"/> (sent with the other statuses); death, the end of the time and a round reset clear it, bots never get it. A map without the file or without
/// a plantation zone has none of this and nothing shows for the hold: no prompt, no ring.
/// </summary>
internal sealed class PlantationServer
{
    readonly ServerContext _ctx;
    readonly PlantationFile _data;
    readonly MapAreas _areas;
    readonly KeyCompetition _competition;
    readonly Sitters _sitters;
    readonly PounceServer _pounce;
    readonly StreakServer _streaks;

    public PlantationServer(ServerContext ctx, PlantationFile data, MapAreas areas, KeyCompetition competition, Sitters sitters, PounceServer pounce, StreakServer streaks)
    {
        _ctx = ctx; _data = data; _areas = areas; _competition = competition; _sitters = sitters; _pounce = pounce; _streaks = streaks;
    }

    /// <summary>
    /// Every input of a player, with the interact key's state (next to <see cref="VillageRadio.RozhlasServer.OnInput"/>): held for <see cref="PlantationFile.HoldSeconds"/> inside a plantation, on foot and alive,
    /// with nothing else in reach of the key, makes the player high (once per hold). Silent otherwise.
    /// </summary>
    public void Use(ServerClient c, bool useDown)
    {
        var counts = !c.IsBot && c.Joined && _areas.ZonePolygons.Has(PlantationRules.Kind) && HoldCounts(c);
        if (c.PlantHold.Tick(useDown, counts, 1.0 / Wire.TickRate, _data.HoldSeconds)) GetHigh(c);
    }

    bool HoldCounts(ServerClient c)
    {
        var alive = _ctx.Match.Get(c.Id)?.Life == PlayerLife.Alive;
        var onFoot = c.Car == null && !_sitters.IsSeated(c.Id) && !_pounce.IsPinned(c.Id) && _ctx.Tick > c.HeldUntilTick && _streaks.Rules.ControlOf(c.Id) == null;
        if (!alive || !onFoot) return false;
        var p = c.Body.GlobalPosition;
        var inside = _areas.ZonePolygons.Contains(PlantationRules.Kind, p.X, p.Z);
        return PlantationRules.HoldCounts(alive, onFoot, inside, inside ? _competition.Of(c) : KeyCompetitor.None);
    }

    /// <summary>
    /// Makes a living human player high: the clock starts (or starts over when they are high already) and the status bit goes on; it reaches everyone with the next tick's flush. False for a bot or a player
    /// who is not alive. The completed hold, and the admin's <c>/status high</c>.
    /// </summary>
    public bool GetHigh(ServerClient c)
    {
        if (c.IsBot || _ctx.Match.Get(c.Id)?.Life != PlayerLife.Alive) return false;
        c.High.Complete((float)_data.HighSeconds);
        c.Status.Grant(StatusKind.High);
        GD.Print($"server: {c.Name} #{c.Id} is high for {_data.HighSeconds:0} s");
        return true;
    }

    /// <summary>Every tick: the clocks of the highs run, and one that has run out takes the status bit away.</summary>
    public void Tick(float dt)
    {
        foreach (var c in _ctx.Players.Values)
        {
            if (!c.High.IsHigh) continue;
            if (c.High.Tick(dt)) c.Status.Remove(StatusKind.High);
        }
    }

    /// <summary>A new round: everyone is sober and no hold is under way (the other statuses stay).</summary>
    public void RoundStart()
    {
        foreach (var c in _ctx.Players.Values)
        {
            c.PlantHold.Reset();
            if (c.High.Clear()) c.Status.Remove(StatusKind.High);
        }
    }
}

using Godot;
using Drycastle.Shooter.Net.Server.Parish;
using Drycastle.Shooter.Net.Server.Seating;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.Shooter.Net.Server.Terrain;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Plantation;
using Drycastle.ShooterCore.Status;

namespace Drycastle.Shooter.Net.Server.Highs;

/// <summary>A Job that turns the plantation's hold into its own (the Harvest heist fills a weed Duffel): asked at every input of a player in a plantation.</summary>
internal interface IPlantationFill
{
    /// <summary>The seconds this player's hold takes when the Job owns it, or null: the plantation as always (High).</summary>
    double? HoldSecondsFor(ServerClient c);
    /// <summary>
    /// What the Job's hold does for this player now (<see cref="FillReach.Off"/>: the Job has no hold for them), and the harvest's maximum. Anything but Ready is the Job owning the hold while it cannot complete (a Harvest
    /// player who carries a Duffel, whose harvest is all filled, or whose van has not reached the plantation): the hold does not count at all, so neither a Duffel nor High, and the player is told why.
    /// </summary>
    FillReach ReachOf(ServerClient c, out int max);
    /// <summary>Seconds of the hold after which the filler is drawn bending and reaching (and the rustle plays).</summary>
    double ShowAfterSeconds { get; }
    /// <summary>The player's hold completed (once per hold).</summary>
    void Completed(ServerClient c);
}

/// <summary>
/// The marijuana plantation on the server (GLOSSARY "Plantation", "High"; plantation.json). A human player on foot and alive inside a <c>plantation</c> zone (the polygons of zone_polygons.json,
/// <see cref="ZonePolygons"/>) who holds the interact key for <see cref="PlantationFile.HoldSeconds"/>, with nothing else in reach of the key, gets high for <see cref="PlantationFile.HighSeconds"/>
/// (<see cref="PlayerHigh"/>, <c>Player.High</c>). The hold is the rozhlas set's <see cref="InteractHold"/>, counted here from the inputs; the key must be released between holds, and another completed hold
/// while high starts the time over. The plantation has the lowest priority of the key (<see cref="PlantationRules.HoldCounts"/>, <see cref="KeyCompetition"/>). The high is the replicated bit
/// <see cref="StatusKind.High"/> of <see cref="ServerClient.Status"/> (sent with the other statuses); death, the end of the time and a round reset clear it, bots never get it. A map without the file or without
/// a plantation zone has none of this and nothing shows for the hold: no prompt, no ring. Only a Harvest's fill (<see cref="IPlantationFill"/>) shows anything: the prompt and why it is blocked
/// (<see cref="EventKind.HarvestFill"/>, told on a change), and the status bit <see cref="StatusKind.Filling"/> while the fill hold runs, so everyone sees the filler bend and reach.
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

    /// <summary>A Harvest heist's hold (docs/cartel.md "Harvest"): for a player of a running Harvest the hold fills a weed Duffel instead of High. Null: the plantation as always.</summary>
    public IPlantationFill? Fill { get; set; }

    /// <summary>
    /// Every input of a player, with the interact key's state (next to <see cref="VillageRadio.RozhlasServer.OnInput"/>): held for <see cref="PlantationFile.HoldSeconds"/> inside a plantation, on foot and alive,
    /// with nothing else in reach of the key, makes the player high (once per hold). Silent otherwise.
    /// </summary>
    public void Use(ServerClient c, bool useDown)
    {
        var inPlantation = !c.IsBot && c.Joined && _areas.ZonePolygons.Has(PlantationRules.Kind) && HoldCounts(c);
        var max = 0;
        var reach = inPlantation && Fill != null ? Fill.ReachOf(c, out max) : FillReach.Off;
        var counts = inPlantation && (Fill == null || reach is FillReach.Off or FillReach.Ready);
        var fill = counts && reach == FillReach.Ready ? Fill?.HoldSecondsFor(c) : null; // a Harvest's player fills a Duffel instead
        var done = c.PlantHold.Tick(useDown, counts, 1.0 / Wire.TickRate, fill ?? _data.HoldSeconds);
        Tell(c, reach, max);
        SetFilling(c, fill != null && HarvestFillRules.Shows(reach, c.PlantHold.State == InteractHoldState.Charging, c.PlantHold.Held, Fill!.ShowAfterSeconds));
        if (!done) return;
        if (fill != null) Fill!.Completed(c);
        else GetHigh(c);
    }

    /// <summary>Tells the player what their hold does when that changed (<see cref="EventKind.HarvestFill"/>); a player never in a Harvest is told nothing.</summary>
    void Tell(ServerClient c, FillReach reach, int max)
    {
        var shown = reach == FillReach.Full ? max : 0; // only "all N filled" says the number
        var key = (ushort)((byte)reach | shown << 8);
        if (key == c.FillTold) return;
        c.FillTold = key;
        _ctx.SendEvent(c, EventKind.HarvestFill, HarvestFillRules.Encode(reach, shown));
    }

    /// <summary>The status bit equals "the fill hold has run long enough".</summary>
    static void SetFilling(ServerClient c, bool on)
    {
        if (on) c.Status.Grant(StatusKind.Filling);
        else c.Status.Remove(StatusKind.Filling);
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
            c.Status.Remove(StatusKind.Filling);
            if (c.High.Clear()) c.Status.Remove(StatusKind.High);
        }
    }
}

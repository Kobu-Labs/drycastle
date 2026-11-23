using Godot;
using Drycastle.Shooter.Buildings;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Plantation;
using Drycastle.ShooterCore.RozhlasFeed;
using Drycastle.ShooterCore.Status;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// The marijuana plantation on the server (GLOSSARY "Plantation", "High"; plantation.json). A human player on foot and alive inside a <c>plantation</c> zone (the polygons of zone_polygons.json,
/// <see cref="ZonePolygons"/>) who holds the interact key for <see cref="PlantationFile.HoldSeconds"/>, with nothing else in reach of the key, gets high for <see cref="PlantationFile.HighSeconds"/>
/// (<see cref="PlayerHigh"/>, <c>Player.High</c>). The hold is the rozhlas set's <see cref="InteractHold"/>, counted here from the inputs; the key must be released between holds, and another completed hold
/// while high starts the time over. The plantation has the lowest priority of the key (<see cref="PlantationRules.HoldCounts"/>, <see cref="KeyCompetitors"/>). The high is the replicated bit
/// <see cref="StatusKind.High"/> of <see cref="ServerClient.Status"/> (sent with the other statuses); death, the end of the time and a round reset clear it, bots never get it. A map without the file or without
/// a plantation zone has none of this and nothing shows for the hold: no prompt, no ring.
/// </summary>
public partial class NetServer
{
    /// <summary>plantation.json (the host sets it from the data directory; the shipped defaults otherwise).</summary>
    public PlantationFile Plantation = PlantationFile.Default;

    ZonePolygons? _zonePolygons;

    /// <summary>The map's zone polygons (zone_polygons.json). Empty without a map or the file; a broken or other-version file is logged and counts as none.</summary>
    ZonePolygons MapZonePolygons
    {
        get
        {
            if (_zonePolygons != null) return _zonePolygons;
            _zonePolygons = ZonePolygons.FromJsonOrEmpty(Map?.ZonePolygonsJson, m => GD.PrintErr($"server: {m}; there are no zone polygons"));
            if (_zonePolygons.Has(PlantationRules.Kind)) GD.Print($"server: plantation: {_zonePolygons.Rings(PlantationRules.Kind).Count} outline(s)");
            return _zonePolygons;
        }
    }

    /// <summary>Whether the point (Godot ground axes) lies inside a plantation.</summary>
    public bool InPlantation(Vector3 at) => MapZonePolygons.Contains(PlantationRules.Kind, at.X, at.Z);

    /// <summary>
    /// Every input of a player, with the interact key's state (next to <see cref="RozhlasServer.OnInput"/>): held for <see cref="PlantationFile.HoldSeconds"/> inside a plantation, on foot and alive,
    /// with nothing else in reach of the key, makes the player high (once per hold). Silent otherwise.
    /// </summary>
    void PlantationUse(ServerClient c, bool useDown)
    {
        var counts = !c.IsBot && c.Joined && MapZonePolygons.Has(PlantationRules.Kind) && PlantationHoldCounts(c);
        if (c.PlantHold.Tick(useDown, counts, 1.0 / Wire.TickRate, Plantation.HoldSeconds)) GetHigh(c);
    }

    bool PlantationHoldCounts(ServerClient c)
    {
        var alive = _match.Get(c.Id)?.Life == PlayerLife.Alive;
        var onFoot = c.Car == null && !_sitters.IsSeated(c.Id) && !_pounce.IsPinned(c.Id) && _tick > c.HeldUntilTick && _streaks.Rules.ControlOf(c.Id) == null;
        if (!alive || !onFoot) return false;
        var inside = InPlantation(c.Body.GlobalPosition);
        return PlantationRules.HoldCounts(alive, onFoot, inside, inside ? KeyCompetitors(c) : KeyCompetitor.None);
    }

    /// <summary>
    /// What else is in reach of the interact key of a player on foot: the things whose own code wins the key over the plantation (corpse, ground item, crate, the garage doors, a ladder, a vehicle, the Mass
    /// seat, the rozhlas set, lying Loot, a Scud truck's rocket cart). Read only: none of them is touched.
    /// </summary>
    KeyCompetitor KeyCompetitors(ServerClient c)
    {
        var p = c.Body.GlobalPosition;
        var at = N(p);
        var k = KeyCompetitor.None;
        if (_corpses.AnyBodyInReach(at)) k |= KeyCompetitor.Corpse;
        if (_corpses.AnyGroundInReach(at)) k |= KeyCompetitor.GroundItem;
        if (_crates.AnyInReach(at)) k |= KeyCompetitor.Crate;
        if (c.Body.State.OnLadder || _areas.Ladders.Reach(at, c.Body.Yaw) >= 0) k |= KeyCompetitor.Ladder;
        if (EnterableCar(p) != null) k |= KeyCompetitor.Vehicle;
        if (_garage.InReach(at)) k |= KeyCompetitor.Doors;
        if (_mass.SeatNear(at)) k |= KeyCompetitor.MassSeat;
        if (_rozhlas.CanTakeSetAt(at, c.Car != null)) k |= KeyCompetitor.RozhlasSet;
        if (_thieves.LootInReach(c) != null) k |= KeyCompetitor.Loot;
        if (ScudReloadOffer(c) != null) k |= KeyCompetitor.ScudCart;
        return k;
    }

    /// <summary>
    /// Makes a living human player high: the clock starts (or starts over when they are high already) and the status bit goes on; it reaches everyone with the next tick's flush. False for a bot or a player
    /// who is not alive. The completed hold, and the admin's <c>/status high</c>.
    /// </summary>
    bool GetHigh(ServerClient c)
    {
        if (c.IsBot || _match.Get(c.Id)?.Life != PlayerLife.Alive) return false;
        c.High.Complete((float)Plantation.HighSeconds);
        c.Status.Grant(StatusKind.High);
        GD.Print($"server: {c.Name} #{c.Id} is high for {Plantation.HighSeconds:0} s");
        return true;
    }

    /// <summary>Every tick: the clocks of the highs run, and one that has run out takes the status bit away.</summary>
    void PlantationTick(float dt)
    {
        foreach (var c in _byId.Values)
        {
            if (!c.High.IsHigh) continue;
            if (c.High.Tick(dt)) c.Status.Remove(StatusKind.High);
        }
    }

    /// <summary>A new round: everyone is sober and no hold is under way (the other statuses stay).</summary>
    void PlantationRoundStart()
    {
        foreach (var c in _byId.Values)
        {
            c.PlantHold.Reset();
            if (c.High.Clear()) c.Status.Remove(StatusKind.High);
        }
    }
}

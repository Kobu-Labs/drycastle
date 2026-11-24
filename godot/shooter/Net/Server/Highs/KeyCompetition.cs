using Drycastle.Shooter.Net.Server.BusGarage;
using Drycastle.Shooter.Net.Server.Crates;
using Drycastle.Shooter.Net.Server.Houses;
using Drycastle.Shooter.Net.Server.Parish;
using Drycastle.Shooter.Net.Server.Terrain;
using Drycastle.Shooter.Net.Server.Thieves;
using Drycastle.Shooter.Net.Server.VillageRadio;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Plantation;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Highs;

/// <summary>
/// What else is in reach of the interact key of a player on foot: the things whose own code wins the key over the plantation (corpse, ground item, crate, the garage doors or a house's, a ladder, a vehicle, the Mass
/// seat, the rozhlas set, lying Loot, a Scud truck's rocket cart). Read only: none of them is touched.
/// </summary>
internal sealed class KeyCompetition
{
    readonly CorpseBook _corpses;
    readonly CrateServer _crates;
    readonly MapAreas _areas;
    readonly GarageServer _garage;
    readonly HouseDoorServer _houses;
    readonly MassServer _mass;
    readonly RozhlasServer _rozhlas;
    readonly ThievesServer _thieves;
    readonly Func<NVec3, bool> _vehicleInReach;
    readonly Func<ServerClient, bool> _scudCartOffered;

    /// <param name="vehicleInReach">A vehicle the key would get a player at the point into (the vehicle step's).</param>
    /// <param name="scudCartOffered">A Scud truck's rocket cart is on offer to the player (the Scud's).</param>
    public KeyCompetition(CorpseBook corpses, CrateServer crates, MapAreas areas, GarageServer garage, HouseDoorServer houses, MassServer mass, RozhlasServer rozhlas, ThievesServer thieves,
        Func<NVec3, bool> vehicleInReach, Func<ServerClient, bool> scudCartOffered)
    {
        _corpses = corpses; _crates = crates; _areas = areas; _garage = garage; _houses = houses; _mass = mass; _rozhlas = rozhlas; _thieves = thieves; _vehicleInReach = vehicleInReach; _scudCartOffered = scudCartOffered;
    }

    public KeyCompetitor Of(ServerClient c)
    {
        var p = c.Body.GlobalPosition;
        var at = new NVec3(p.X, p.Y, p.Z);
        var k = KeyCompetitor.None;
        if (_corpses.AnyBodyInReach(at)) k |= KeyCompetitor.Corpse;
        if (_corpses.AnyGroundInReach(at)) k |= KeyCompetitor.GroundItem;
        if (_crates.AnyInReach(at)) k |= KeyCompetitor.Crate;
        if (c.Body.State.OnLadder || _areas.Ladders.Reach(at, c.Body.Yaw) >= 0) k |= KeyCompetitor.Ladder;
        if (_vehicleInReach(at)) k |= KeyCompetitor.Vehicle;
        if (_garage.InReach(at) || _houses.InReach(at)) k |= KeyCompetitor.Doors;
        if (_mass.SeatNear(at)) k |= KeyCompetitor.MassSeat;
        if (_rozhlas.CanTakeSetAt(at, c.Car != null)) k |= KeyCompetitor.RozhlasSet;
        if (_thieves.LootInReach(c) != null) k |= KeyCompetitor.Loot;
        if (_scudCartOffered(c)) k |= KeyCompetitor.ScudCart;
        return k;
    }
}

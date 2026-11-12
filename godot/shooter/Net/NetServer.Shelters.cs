using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Server side of the shelter rule (#224, ADR 0009): a bunker room shelters the players inside from a blast outside it, and a
/// blast inside a room stays in it. Asked by every blast that splashes players (rockets and flak shells, the attack missile, the
/// gunship, a vehicle blowing up). The MOAB does not ask: it kills everyone. A direct hit does not ask either.
/// </summary>
public partial class NetServer
{
    Shelters? _shelters;

    /// <summary>The bunker rooms of the map. Empty without a map or a bunkers.json; a broken file is logged and counts as none.</summary>
    Shelters MapShelters
    {
        get
        {
            if (_shelters != null) return _shelters;
            _shelters = Shelters.FromJsonOrEmpty(Map?.BunkersJson, m => GD.PrintErr($"server: {m}; bunkers give no shelter from blasts and no ladders"));
            if (_shelters.Rooms.Count > 0) GD.Print($"server: {_shelters.Rooms.Count} bunker rooms shelter from blasts");
            return _shelters;
        }
    }

    /// <summary>The ladders of the map, given to every body the server simulates so it climbs as the client predicts.</summary>
    Ladders MapLadders => MapShelters.Ladders;

    /// <summary>Does the splash of a blast at <paramref name="blast"/> reach <paramref name="t"/>? False when a bunker room's walls are between them.</summary>
    bool BlastReaches(NVec3 blast, HitTarget t) => MapShelters.Reaches(blast, HitMath.Centre(t));
}

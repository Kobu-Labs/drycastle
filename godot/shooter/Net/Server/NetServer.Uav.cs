using Drycastle.ShooterCore.Flight;
using Drycastle.ShooterCore.Killstreaks.Uav;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// The UAV on the server (issue #121). <see cref="UavStreak"/> decides when pulses go out; this file supplies the living
/// players and their Ghost-style visibility, and sends each pulse to the owner alone (no positions reach anyone else). The UAV
/// aircraft (#303) is an ownerless <see cref="Aircraft"/> over the map: ownerless so no client mistakes it for a gunship to steer.
/// </summary>
public partial class NetServer
{
    UavStreak _uav = null!;

    /// <summary>Built with the other streaks: the UAV's pulses go to the owner over the same reliable channel.</summary>
    UavStreak BuildUav() => new(UavContacts, (owner, kind, payload) => { if (_byId.TryGetValue(owner, out var c)) Send(c, new EventMsg((byte)kind, payload)); },
        () => SpawnAircraftOverMap(UavStreak.AircraftKind), id => _match.Entities.Despawn(id, WorldDespawnReason.Removed));

    /// <summary>Living players with their UAV visibility (the perk's <c>uav_visibility</c>; 0 hides them).</summary>
    IReadOnlyList<UavContact> UavContacts() =>
        _byId.Values.Where(o => _match.Get(o.Id)?.Life == PlayerLife.Alive)
            .Select(o => new UavContact(o.Id, o.Body.GlobalPosition.X, o.Body.GlobalPosition.Z, o.Perks.UavVisibility, _match.Get(o.Id)?.Team ?? Teams.None)).Concat(TargetUavContacts()).ToList();
}

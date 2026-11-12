using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The UAV on the server (issue #121). <see cref="UavStreak"/> decides when pulses go out; this file supplies the living
/// players and their Ghost-style visibility, and sends each pulse to the owner alone (no positions reach anyone else).
/// </summary>
public partial class NetServer
{
    UavStreak _uav = null!;

    /// <summary>Built with the other streaks: the UAV's pulses go to the owner over the same reliable channel.</summary>
    UavStreak BuildUav() => new(UavContacts, (owner, kind, payload) => { if (_byId.TryGetValue(owner, out var c)) Send(c, new EventMsg((byte)kind, payload)); });

    /// <summary>Living players with their UAV visibility (the perk's <c>uav_visibility</c>; 0 hides them).</summary>
    IReadOnlyList<UavContact> UavContacts() =>
        _byId.Values.Where(o => _match.Get(o.Id)?.Life == PlayerLife.Alive)
            .Select(o => new UavContact(o.Id, o.Body.GlobalPosition.X, o.Body.GlobalPosition.Z, o.Perks.UavVisibility, _match.Get(o.Id)?.Team ?? Teams.None)).Concat(TargetUavContacts()).ToList();
}

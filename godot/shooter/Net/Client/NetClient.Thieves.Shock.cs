using Godot;
using Drycastle.Shooter.Thieves;

namespace Drycastle.Shooter.Net.Client;

/// <summary>A Thief electrocuted at the top of a pole (docs/events/thieves/07), as the client shows it: the beams run from the man to the wire seats of the pole beside him for the shock's seconds (<see cref="ShockFx"/>).</summary>
public partial class NetClient
{
    /// <summary>How far from the man (flat) a pole's wire seats still take part: the man stands beside the column, the arms reach about two metres.</summary>
    const float ShockPoleM = 3f;

    void OnThiefShocked(ushort memberId, Vector3 at)
    {
        var seats = Map == null ? new List<System.Numerics.Vector3>() : Map.WireSeatsNear(new System.Numerics.Vector3(at.X, at.Y, at.Z), ShockPoleM);
        var wires = seats.Select(s => new Vector3(s.X, s.Y, s.Z)).ToArray();
        var view = _gang.TryGetValue(memberId, out var g) ? g.View : null;
        ShockFx.Play(Owned, () => (view != null && GodotObject.IsInstanceValid(view) ? view.GlobalPosition : at) + Vector3.Up * 1.1f, wires, ThievesData.Loot.Connector.ShockSeconds);
    }
}

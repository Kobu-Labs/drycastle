using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Citizens on the client: the world entities of type <see cref="CitizenRules.TypeId"/>. Each is a <see cref="CitizenView"/> (the body comes
/// from holy_mass.json by the look number in the state's owner field), placed from the interpolated state and shown walking, seated or dead.
/// The server owns the citizen; the client only draws it.
/// </summary>
public partial class NetClient
{
    /// <summary>holy_mass.json: set by the host (the bodies of the citizens).</summary>
    public HolyMassFile HolyMassData = new();

    sealed class CitizenNode
    {
        public CitizenView View = null!;
    }

    readonly Dictionary<ushort, CitizenNode> _citizens = new();

    /// <summary>Called once from <see cref="_Ready"/>: registers the citizen type and follows its entities.</summary>
    void InitCitizens()
    {
        CitizenRules.Register(WorldEntityRegistry.Shared);
        WorldEntities.Spawned += OnCitizenSpawned;
        WorldEntities.Despawned += OnCitizenDespawned;
    }

    void OnCitizenSpawned(WorldReplica.Entity e)
    {
        if (e.Type != CitizenRules.TypeId) return;
        if (HolyMassData.Citizens.Models.Count == 0)
        {
            GD.PrintErr($"client: citizen {e.Id} but holy_mass.json has no models; not drawn (data files differ from the server's?)");
            return;
        }
        var view = CitizenView.Make(HolyMassData.Citizens, HolyMassData.Mass, e.State.Owner, Citizen.RoleOf(e.State.Hp));
        view.Name = $"Citizen{e.Id}";
        Owned.AddChild(view);
        _citizens[e.Id] = new CitizenNode { View = view };
    }

    void OnCitizenDespawned(ushort id)
    {
        if (_citizens.Remove(id, out var c)) c.View.QueueFree();
    }

    /// <summary>Places the citizens (interpolated, a little in the past like every remote body) and shows the state they are in.</summary>
    void RenderCitizens()
    {
        if (_citizens.Count == 0 || _lastTick == 0) return;
        var rt = RenderTickNow();
        var hideIn = ChurchInterior.Visible ? null : ChurchDoorNode.Current?.Shell; // doors shut, camera outside: whoever is in the church is not drawn
        foreach (var (id, c) in _citizens)
        {
            if (!WorldEntities.Entities.TryGetValue(id, out var e)) continue;
            if (e.Buf.At(rt) is { } s)
            {
                c.View.GlobalPosition = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
                c.View.Rotation = new Vector3(0, s.Yaw, 0);
            }
            var gp = c.View.GlobalPosition;
            c.View.Visible = hideIn == null || !hideIn.InRooms(new System.Numerics.Vector3(gp.X, gp.Y, gp.Z));
            c.View.Show(CitizenRules.StateOf(e.State.State));
            c.View.SetMass(_massNow.Chanting, _massNow.PastorKills, new Vector3(_massNow.ChantAt.X, _massNow.ChantAt.Y, _massNow.ChantAt.Z));
        }
    }
}

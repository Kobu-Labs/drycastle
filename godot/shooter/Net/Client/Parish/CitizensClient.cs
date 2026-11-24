using Godot;
using Drycastle.Shooter.Churches;
using Drycastle.Shooter.Citizens;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.Parish;

/// <summary>
/// Citizens on the client: the world entities of type <see cref="CitizenRules.TypeId"/>. Each is a <see cref="CitizenView"/> (the body comes
/// from holy_mass.json by the look number in the state's owner field), placed from the interpolated state and shown walking, seated or dead.
/// The server owns the citizen; the client only draws it.
/// </summary>
public sealed class CitizensClient
{
    readonly ClientContext _ctx;
    readonly HolyMassFile _data;
    readonly MassClient _mass;
    readonly Dictionary<ushort, CitizenView> _views = new();

    /// <summary>Registers the citizen type and follows its entities.</summary>
    public CitizensClient(ClientContext ctx, HolyMassFile data, MassClient mass)
    {
        _ctx = ctx;
        _data = data;
        _mass = mass;
        CitizenRules.Register(WorldEntityRegistry.Shared);
        ctx.WorldEntities.Spawned += OnSpawned;
        ctx.WorldEntities.Despawned += OnDespawned;
    }

    public IEnumerable<CitizenView> Views => _views.Values;

    /// <summary>The views went with <see cref="ClientContext.Owned"/>: forget them.</summary>
    public void Reset() => _views.Clear();

    void OnSpawned(WorldReplica.Entity e)
    {
        if (e.Type != CitizenRules.TypeId) return;
        if (_data.Citizens.Models.Count == 0)
        {
            GD.PrintErr($"client: citizen {e.Id} but holy_mass.json has no models; not drawn (data files differ from the server's?)");
            return;
        }
        var view = CitizenView.Make(_data.Citizens, _data.Mass, e.State.Owner, Citizen.RoleOf(e.State.Hp));
        view.Name = $"Citizen{e.Id}";
        _ctx.Owned.AddChild(view);
        _views[e.Id] = view;
    }

    void OnDespawned(ushort id)
    {
        if (_views.Remove(id, out var v)) v.QueueFree();
    }

    /// <summary>Places the citizens (interpolated, a little in the past like every remote body) and shows the state they are in.</summary>
    public void Render()
    {
        if (_views.Count == 0 || _ctx.LastTick == 0) return;
        var rt = _ctx.RenderTick;
        var hideIn = ChurchInterior.Visible ? null : ChurchDoorNode.Current?.Shell; // doors shut, camera outside: whoever is in the church is not drawn
        var mass = _mass.State;
        foreach (var (id, view) in _views)
        {
            if (!_ctx.WorldEntities.Entities.TryGetValue(id, out var e)) continue;
            if (e.Buf.At(rt) is { } s)
            {
                view.GlobalPosition = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
                view.Rotation = new Vector3(0, s.Yaw, 0);
            }
            var gp = view.GlobalPosition;
            view.Visible = hideIn == null || !hideIn.InRooms(new System.Numerics.Vector3(gp.X, gp.Y, gp.Z));
            view.Show(CitizenRules.StateOf(e.State.State));
            view.SetMass(mass.Chanting, mass.PastorKills, new Vector3(mass.ChantAt.X, mass.ChantAt.Y, mass.ChantAt.Z));
        }
    }
}

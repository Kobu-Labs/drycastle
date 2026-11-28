using Godot;
using Drycastle.Shooter.Net.Client.Aviation;
using Drycastle.Shooter.Net.Client.Incidents;
using Drycastle.Shooter.Net.Client.Streaks;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Minimap;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.HudFeeds;

/// <summary>The minimap's moving parts each frame: where the player stands and which way the map turns, the vehicles that are shown, and the team mates and squad mates.</summary>
internal sealed class MinimapFeed
{
    readonly ClientContext _ctx;
    readonly MissileClient _missile;
    readonly AircraftClient _aircraft;
    readonly IntelClient _intel;
    readonly List<Vector2> _carDots = new();
    readonly List<Vector2> _friendDots = new();
    readonly List<(Vector2 At, string Name)> _squadDots = new();

    public MinimapFeed(ClientContext ctx, MissileClient missile, AircraftClient aircraft, IntelClient intel)
    {
        _ctx = ctx; _missile = missile; _aircraft = aircraft; _intel = intel;
    }

    /// <summary>Anyone but me who is not a teammate (FFA: everyone else).</summary>
    bool IsMapEnemy(ushort id) => id != _ctx.MyId && !_ctx.Match.AreFriends(_ctx.MyId, id);

    public void Update()
    {
        if (_ctx.Me == null || _ctx.Hud == null) return;
        var g = _ctx.Me.GlobalPosition;
        var src = MinimapView.Select(missilePov: _missile.PovOn, turret: _aircraft.AtTurret, driving: _ctx.Driving);
        _ctx.Hud.Minimap.SetPlayer(new Vector2(g.X, g.Z), MinimapView.YawFor(src, _ctx.Me.Yaw, _ctx.Cars.DrivenYaw, _aircraft.TurretViewYaw, _missile.ViewYaw));
        _carDots.Clear();
        foreach (var c in _ctx.Cars.All)
        {
            if (c.Destroyed) continue;
            _ctx.Cars.SeatOccupants.TryGetValue(c.Id, out var others); // an enemy on any seat: not on the map (#313)
            if (HudLogic.HideEnemyOccupiedVehicle(c.Driver, others, _ctx.MyId, IsMapEnemy)) continue;
            if (_ctx.Vehicles != null && c.Kind < _ctx.Vehicles.Vehicles.Count && _intel.ScudTruckHidden(_ctx.Vehicles.Vehicles[c.Kind])) continue; // the Scud event's truck is not on any map until it is found
            _carDots.Add(new Vector2(c.Node.GlobalPosition.X, c.Node.GlobalPosition.Z));
        }
        _ctx.Hud.Minimap.SetVehicleDots(_carDots);
        _friendDots.Clear();
        _squadDots.Clear();
        if (_ctx.Match.HasSquads) // open world: friends are squad mates, so all blue
            foreach (var (id, r) in _ctx.Remotes)
                if (r.State != EntityState.Dead && _ctx.Match.AreFriends(_ctx.MyId, id) && GodotObject.IsInstanceValid(r.Node))
                {
                    var at = new Vector2(r.Node.GlobalPosition.X, r.Node.GlobalPosition.Z);
                    if (_ctx.Match.AreSquadMates(_ctx.MyId, id)) _squadDots.Add((at, _ctx.Match.NameOf(id))); // squad mates are blue with a name, the other teammates green
                    else _friendDots.Add(at);
                }
        _ctx.Hud.Minimap.SetFriendDots(_friendDots);
        _ctx.Hud.Minimap.SetSquadDots(_squadDots);
    }
}

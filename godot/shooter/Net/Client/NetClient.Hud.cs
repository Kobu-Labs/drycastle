using Godot;
using Drycastle.Shooter.HeadsUp;
using Drycastle.Shooter.Loudspeaker;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Minimap;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Client;

/// <summary>Feeds the in-match <see cref="Hud"/> from client state: events push changes, the per-frame hook only forwards health.</summary>
public partial class NetClient
{
    /// <summary>
    /// The match's phase edges, detected once in <see cref="ClientMatchState"/> and handed to the views that react to them: the round sounds, and the kill feed
    /// that a restarted round (the end screen's end, or an admin's restart mid-round) empties.
    /// </summary>
    void InitMatchEdges()
    {
        Match.PhaseChanged += OnPhaseChanged;
        Match.RoundRestarted += () => { Ctx.Hud?.ClearKills(); _harvester.RoundRestart(); };
    }


    /// <summary>Spread the next shot would get, for the crosshair gap: the same rule as the server's shot (stance, aim, movement).</summary>
    float CrosshairSpread()
    {
        if (Ctx.CurrentWeapon is not { } w) return 0f;
        var own = HitMath.OwnSpread(w, _weapon.Spread.Degrees(w, Ctx.Now));
        // in an armed seat the vehicle's speed is what widens it (the server's rule for a shot from the seat)
        if (Ctx.View.InArmedSeat && Ctx.Cars.MySeat() is { } m) return SeatAimRules.ShotSpread(own, w, _weapon.AimHeld, SeatsClient.RideSpeed(m.Car, _driveClient.StateOf(m.Car)), m.Seat);
        return Ctx.Me is { } me ? HitMath.ShotSpread(own, w, me.State, _weapon.AimHeld) : own;
    }

    void HudWelcome()
    {
        if (!ShotFx.Enabled || Ctx.Hud != null) return; // headless runs have no HUD
        Ctx.Hud = new Hud { MyId = Ctx.MyId, KillWeaponName = id => Weapons != null && id < Weapons.Weapons.Count ? ClientText.WeaponName(Weapons.Weapons[id].Id) : null, Scoped = () => Ctx.Me is { ZoomFov: > 0 } && !_binoculars, Binoculars = BinocularsView, Aim = () => _viewmodel.AimAmount, Breath = _aim.BreathView, Scope = _aim.ScopeView, Spread = CrosshairSpread };
        AddChild(Ctx.Hud); // the hotbar names are set at each spawn (ApplySpawnLoadout)
        Ctx.Hud.TacticalMapTeleport = _screens.OnMapTeleport;
        Sandbox.ShowOnHud(); // the SANDBOX badge (#156)
        Loadouts.WireHud(Ctx.Hud);
        Ctx.Hud.AttachEquipment(this); // F (#268)
        Ctx.Hud.AttachHuntersLog(_hunts.Environment()); // J (#336)
        Ctx.Hud.AttachMineralLog(); // N
        _hunts.Data.RequestLog(0); // learns the player's own id in the log (their own announcements say "you"), and warms My Log
        Ctx.Hud.Minimap.Build(Map); // once per connection; the texture comes from the map's parcels and roads (ticket #119)
        Ctx.Hud.PrewarmTacticalMap(GetViewport().GetVisibleRect().Size); // the full-screen map's picture is made on a worker meanwhile
    }

    /// <summary>The loaded map (set by the host): the minimap's texture source.</summary>
    public Drycastle.MapLoader.MapLoader? Map { get => Ctx.Map; set => Ctx.Map = value; }
    readonly List<Vector2> _carDots = new();

    /// <summary>Ammo of the hotbar: the selected hotbar cell, and every hotbar weapon's magazine in hotbar order.</summary>
    void HudAmmo()
    {
        if (Ctx.Hud == null || Ammo is not { } a || Weapons == null || a.Slot < 1 || a.Slot > Weapons.Weapons.Count) return;
        var w = Weapons.Weapons[a.Slot - 1];
        var all = new List<(int, int, bool)>();
        foreach (var gi in Ctx.Hotbar.Slots)
            if (gi < a.Slots.Count) all.Add((a.Slots[gi].Mag, a.Slots[gi].Reserve, Weapons.Weapons[gi].MagazineSize == 0));
        var (mag, reserve) = a.Slots[a.Slot - 1];
        Ctx.Hud.SetAmmo(Math.Max(1, Ctx.Hotbar.PosOf(a.Slot) + 1), w.MagazineSize == 0 ? -1 : mag, reserve, all, a.ReloadRemaining, w.ReloadTime);
    }


    /// <summary>What the hit diagram and the Hunter's Log draw an animal from (shapes, baked pictures, plain bodies).</summary>

    void HudHit(HitMsg h)
    {
        // a gunner's hit marker is the turret overlay's while the HUD is hidden (ticket #152); the hit event reaches it either way
        if (h.Attacker == Ctx.MyId && h.Victim != Ctx.MyId) _aircraftClient.HitMarker(h.Headshot, h.Kill);
        if (Ctx.Hud == null) return;
        if (h.Attacker == Ctx.MyId && h.Victim != Ctx.MyId) Ctx.Hud.Hit(h.Headshot, h.Kill);
        if (h.Victim != Ctx.MyId || Ctx.Me == null) return;
        // the chevron follows its source while it shows (#424); a source that is gone leaves it at its last angle
        if (h.Attacker != Ctx.MyId && Ctx.Remotes.TryGetValue(h.Attacker, out var r)) DamageFrom(h.Attacker, () => NodePos(r.Node));
        else if (GangRules.IsHitId(h.Attacker)) DamageFrom(h.Attacker, () => _thieves.NearestGangMember(Ctx.Me.GlobalPosition)); // a blow of a Gang member: the marker points at the nearest one
        else if (RaidVanRules.IsHitId(h.Attacker)) DamageFrom(h.Attacker, () => _thieves.NearestVan(Ctx.Me.GlobalPosition)); // a Raid van ran into the player: the marker points at the nearest van
        else if (h.Attacker == BigfootRules.TargetId) DamageFrom(h.Attacker, () => _bigfoot.FirstNode is { } giant ? NodePos(giant) : null); // the Bigfoot's logs, shockwave and launch: the marker points at it (#264)
    }

    static Vector3? NodePos(Node3D n) => GodotObject.IsInstanceValid(n) && n.IsInsideTree() ? n.GlobalPosition : null;

    void DamageFrom(object key, Func<Vector3?> source) => Ctx.Hud!.Damage(key, () =>
    {
        if (Ctx.Me == null || !GodotObject.IsInstanceValid(Ctx.Me) || source() is not { } s) return null;
        var m = Ctx.Me.GlobalPosition;
        return HudLogic.DamageAngle(m.X, m.Z, Ctx.Me.Yaw, s.X, s.Z);
    });

    readonly List<Vector2> _friendDots = new();
    readonly List<(Vector2 At, string Name)> _squadDots = new();

    /// <summary>Anyone but me who is not a teammate (FFA: everyone else).</summary>
    bool IsMapEnemy(ushort id) => id != Ctx.MyId && !Match.AreFriends(Ctx.MyId, id);

    void UpdateHud()
    {
        Ctx.Hud?.SetControlMode(Mode.RemoteViewOwnsScreen); // one mode switch: a turret or the missile owns the screen (ticket #152)
        Ctx.Hud?.SetHealth(Mathf.CeilToInt(Ctx.Me?.Health ?? 100));
        Ctx.Hud?.SetSnapshotCount(_snaps); // the FPS card's TPS line (Hud.Fps.cs)
        Ctx.Hud?.CoverCrosshair(_loot.ChooserOpen);
        Ctx.Hud?.SetUnarmedSeat(Ctx.View.InUnarmedSeat); // no crosshair, no ammo readout where nothing can be fired (#373)
        _screens.Update();
        _uav.Tick();
        if (Ctx.Me != null) RozhlasFeedPlayer.Current?.SetListener(Ctx.Me.GlobalPosition);
        if (Ctx.Me != null) Rozhlas.Current?.SetListener(Ctx.Me.GlobalPosition); // the loudspeaker is heard where the player is, not where a vehicle's orbiting camera is
        if (Ctx.Me != null && Ctx.Hud != null)
        {
            var g = Ctx.Me.GlobalPosition;
            var src = MinimapView.Select(missilePov: _missile.PovOn, turret: _aircraftClient.AtTurret, driving: Ctx.Driving);
            Ctx.Hud.Minimap.SetPlayer(new Vector2(g.X, g.Z), MinimapView.YawFor(src, Ctx.Me.Yaw, _carYaw, _aircraftClient.TurretViewYaw, _missile.ViewYaw));
            _carDots.Clear();
            foreach (var c in Ctx.Cars.All)
            {
                if (c.Destroyed) continue;
                Ctx.Cars.SeatOccupants.TryGetValue(c.Id, out var others); // an enemy on any seat: not on the map (#313)
                if (HudLogic.HideEnemyOccupiedVehicle(c.Driver, others, Ctx.MyId, IsMapEnemy)) continue;
                if (Vehicles != null && c.Kind < Vehicles.Vehicles.Count && _intel.ScudTruckHidden(Vehicles.Vehicles[c.Kind])) continue; // the Scud event's truck is not on any map until it is found
                _carDots.Add(new Vector2(c.Node.GlobalPosition.X, c.Node.GlobalPosition.Z));
            }
            Ctx.Hud.Minimap.SetVehicleDots(_carDots);
            _friendDots.Clear();
            _squadDots.Clear();
            if (Match.IsTeamMode)
                foreach (var (id, r) in Ctx.Remotes)
                    if (r.State != EntityState.Dead && Match.AreFriends(Ctx.MyId, id) && GodotObject.IsInstanceValid(r.Node))
                    {
                        var at = new Vector2(r.Node.GlobalPosition.X, r.Node.GlobalPosition.Z);
                        if (Match.AreSquadMates(Ctx.MyId, id)) _squadDots.Add((at, Match.NameOf(id))); // squad mates are blue with a name, the other teammates green
                        else _friendDots.Add(at);
                    }
            Ctx.Hud.Minimap.SetFriendDots(_friendDots);
            Ctx.Hud.Minimap.SetSquadDots(_squadDots);
        }
    }
}

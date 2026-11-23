using Godot;
using Drycastle.Shooter.HeadsUp;
using Drycastle.Shooter.Hunts;
using Drycastle.Shooter.Loudspeaker;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Minimap;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Net.Client;

/// <summary>Feeds the in-match <see cref="Hud"/> from client state: events push changes, the per-frame hook only forwards health.</summary>
public partial class NetClient
{
    Hud? _hud { get => Ctx.Hud; set => Ctx.Hud = value; }
    readonly SpreadEstimator _spread = new();
    /// <summary>The aim button of the last input tick; the crosshair spread uses it as the server uses the input's Aim bit.</summary>
    bool _aimHeld;

    /// <summary>
    /// The match's phase edges, detected once in <see cref="ClientMatchState"/> and handed to the views that react to them: the round sounds, and the kill feed
    /// that a restarted round (the end screen's end, or an admin's restart mid-round) empties.
    /// </summary>
    void InitMatchEdges()
    {
        Match.PhaseChanged += SfxPhase;
        Match.RoundRestarted += () => { _hud?.ClearKills(); HarvesterRoundRestart(); };
    }

    WeaponDef? CurrentWeapon => Ammo is { } a && Weapons != null && a.Slot >= 1 && a.Slot <= Weapons.Weapons.Count ? Weapons.Weapons[a.Slot - 1] : null;

    /// <summary>Spread the next shot would get, for the crosshair gap: the same rule as the server's shot (stance, aim, movement).</summary>
    float CrosshairSpread()
    {
        if (CurrentWeapon is not { } w) return 0f;
        var own = HitMath.OwnSpread(w, _spread.Degrees(w, Now));
        // in an armed seat the vehicle's speed is what widens it (the server's rule for a shot from the seat)
        if (InArmedSeat && Ctx.Cars.MySeat() is { } m) return SeatAimRules.ShotSpread(own, w, _aimHeld, RideSpeed(m.Car, DriveOf(m.Car)), m.Seat);
        return _me is { } me ? HitMath.ShotSpread(own, w, me.State, _aimHeld) : own;
    }

    void HudWelcome()
    {
        if (!ShotFx.Enabled || _hud != null) return; // headless runs have no HUD
        _hud = new Hud { MyId = _myId, KillWeaponName = id => Weapons != null && id < Weapons.Weapons.Count ? ClientText.WeaponName(Weapons.Weapons[id].Id) : null, Scoped = () => _me is { ZoomFov: > 0 } && !_binoculars, Binoculars = BinocularsView, Aim = () => _viewmodel?.AimAmount ?? 0f, Breath = BreathView, Scope = ScopeView, Spread = CrosshairSpread };
        AddChild(_hud); // the hotbar names are set at each spawn (ApplySpawnLoadout)
        _hud.TacticalMapTeleport = _screens.OnMapTeleport;
        Sandbox.ShowOnHud(); // the SANDBOX badge (#156)
        Loadouts.WireHud(_hud);
        _hud.AttachEquipment(this); // F (#268)
        _hud.AttachHuntersLog(_hunts.Environment()); // J (#336)
        _hud.AttachMineralLog(); // N
        _hunts.Data.RequestLog(0); // learns the player's own id in the log (their own announcements say "you"), and warms My Log
        _hud.Minimap.Build(Map); // once per connection; the texture comes from the map's parcels and roads (ticket #119)
        _hud.PrewarmTacticalMap(GetViewport().GetVisibleRect().Size); // the full-screen map's picture is made on a worker meanwhile
    }

    /// <summary>The loaded map (set by the host): the minimap's texture source.</summary>
    public Drycastle.MapLoader.MapLoader? Map { get => Ctx.Map; set => Ctx.Map = value; }
    readonly List<Vector2> _carDots = new();

    /// <summary>Ammo of the hotbar: the selected hotbar cell, and every hotbar weapon's magazine in hotbar order.</summary>
    void HudAmmo()
    {
        if (_hud == null || Ammo is not { } a || Weapons == null || a.Slot < 1 || a.Slot > Weapons.Weapons.Count) return;
        var w = Weapons.Weapons[a.Slot - 1];
        var all = new List<(int, int, bool)>();
        foreach (var gi in Ctx.Hotbar.Slots)
            if (gi < a.Slots.Count) all.Add((a.Slots[gi].Mag, a.Slots[gi].Reserve, Weapons.Weapons[gi].MagazineSize == 0));
        var (mag, reserve) = a.Slots[a.Slot - 1];
        _hud.SetAmmo(Math.Max(1, Ctx.Hotbar.PosOf(a.Slot) + 1), w.MagazineSize == 0 ? -1 : mag, reserve, all, a.ReloadRemaining, w.ReloadTime);
    }


    /// <summary>What the hit diagram and the Hunter's Log draw an animal from (shapes, baked pictures, plain bodies).</summary>

    void HudHit(HitMsg h)
    {
        // a gunner's hit marker is the turret overlay's while the HUD is hidden (ticket #152); the hit event reaches it either way
        if (h.Attacker == _myId && h.Victim != _myId && _gunship != null) _gunOverlay.Hit(h.Headshot, h.Kill);
        if (_hud == null) return;
        if (h.Attacker == _myId && h.Victim != _myId) _hud.Hit(h.Headshot, h.Kill);
        if (h.Victim != _myId || _me == null) return;
        // the chevron follows its source while it shows (#424); a source that is gone leaves it at its last angle
        if (h.Attacker != _myId && _remotes.TryGetValue(h.Attacker, out var r)) DamageFrom(h.Attacker, () => NodePos(r.Node));
        else if (GangRules.IsHitId(h.Attacker)) DamageFrom(h.Attacker, () => _thieves.NearestGangMember(_me.GlobalPosition)); // a blow of a Gang member: the marker points at the nearest one
        else if (RaidVanRules.IsHitId(h.Attacker)) DamageFrom(h.Attacker, () => _thieves.NearestVan(_me.GlobalPosition)); // a Raid van ran into the player: the marker points at the nearest van
        else if (h.Attacker == BigfootRules.TargetId) DamageFrom(h.Attacker, () => _bigfoots.Values.FirstOrDefault() is { } giant ? NodePos(giant.Node) : null); // the Bigfoot's logs, shockwave and launch: the marker points at it (#264)
    }

    static Vector3? NodePos(Node3D n) => GodotObject.IsInstanceValid(n) && n.IsInsideTree() ? n.GlobalPosition : null;

    void DamageFrom(object key, Func<Vector3?> source) => _hud!.Damage(key, () =>
    {
        if (_me == null || !GodotObject.IsInstanceValid(_me) || source() is not { } s) return null;
        var m = _me.GlobalPosition;
        return HudLogic.DamageAngle(m.X, m.Z, _me.Yaw, s.X, s.Z);
    });

    readonly List<Vector2> _friendDots = new();
    readonly List<(Vector2 At, string Name)> _squadDots = new();

    /// <summary>Anyone but me who is not a teammate (FFA: everyone else).</summary>
    bool IsMapEnemy(ushort id) => id != _myId && !Match.AreFriends(_myId, id);

    void UpdateHud()
    {
        _hud?.SetControlMode(Mode.RemoteViewOwnsScreen); // one mode switch: a turret or the missile owns the screen (ticket #152)
        _hud?.SetHealth(Mathf.CeilToInt(_me?.Health ?? 100));
        _hud?.SetSnapshotCount(_snaps); // the FPS card's TPS line (Hud.Fps.cs)
        _hud?.CoverCrosshair(_chooser.IsOpen);
        _hud?.SetUnarmedSeat(InUnarmedSeat); // no crosshair, no ammo readout where nothing can be fired (#373)
        _screens.Update();
        _uav.Tick();
        if (_me != null) RozhlasFeedPlayer.Current?.SetListener(_me.GlobalPosition);
        if (_me != null) Rozhlas.Current?.SetListener(_me.GlobalPosition); // the loudspeaker is heard where the player is, not where a vehicle's orbiting camera is
        if (_me != null && _hud != null)
        {
            var g = _me.GlobalPosition;
            var src = MinimapView.Select(missilePov: _povCam is { Current: true }, turret: _gunship != null, driving: Ctx.Driving);
            _hud.Minimap.SetPlayer(new Vector2(g.X, g.Z), MinimapView.YawFor(src, _me.Yaw, _carYaw, _turretViewYaw, _missileViewYaw));
            _carDots.Clear();
            foreach (var c in Ctx.Cars.All)
            {
                if (c.Destroyed) continue;
                Ctx.Cars.SeatOccupants.TryGetValue(c.Id, out var others); // an enemy on any seat: not on the map (#313)
                if (HudLogic.HideEnemyOccupiedVehicle(c.Driver, others, _myId, IsMapEnemy)) continue;
                if (Vehicles != null && c.Kind < Vehicles.Vehicles.Count && _intel.ScudTruckHidden(Vehicles.Vehicles[c.Kind])) continue; // the Scud event's truck is not on any map until it is found
                _carDots.Add(new Vector2(c.Node.GlobalPosition.X, c.Node.GlobalPosition.Z));
            }
            _hud.Minimap.SetVehicleDots(_carDots);
            _friendDots.Clear();
            _squadDots.Clear();
            if (Match.IsTeamMode)
                foreach (var (id, r) in _remotes)
                    if (r.State != EntityState.Dead && Match.AreFriends(_myId, id) && GodotObject.IsInstanceValid(r.Node))
                    {
                        var at = new Vector2(r.Node.GlobalPosition.X, r.Node.GlobalPosition.Z);
                        if (Match.AreSquadMates(_myId, id)) _squadDots.Add((at, Match.NameOf(id))); // squad mates are blue with a name, the other teammates green
                        else _friendDots.Add(at);
                    }
            _hud.Minimap.SetFriendDots(_friendDots);
            _hud.Minimap.SetSquadDots(_squadDots);
        }
    }
}

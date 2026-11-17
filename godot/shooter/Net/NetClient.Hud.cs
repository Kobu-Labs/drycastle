using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>Feeds the in-match <see cref="Hud"/> from client state: events push changes, the per-frame hook only forwards health.</summary>
public partial class NetClient
{
    Hud? _hud;
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
        if (InArmedSeat && MySeat() is { } m) return SeatAimRules.ShotSpread(own, w, _aimHeld, RideSpeed(m.Car, DriveOf(m.Car)), m.Seat);
        return _me is { } me ? HitMath.ShotSpread(own, w, me.State, _aimHeld) : own;
    }

    void HudWelcome()
    {
        if (!ShotFx.Enabled || _hud != null) return; // headless runs have no HUD
        _hud = new Hud { MyId = _myId, KillWeaponName = id => Weapons != null && id < Weapons.Weapons.Count ? WeaponName(Weapons.Weapons[id].Id) : null, Scoped = () => _me is { ZoomFov: > 0 } && !_binoculars, Binoculars = BinocularsView, Aim = () => _viewmodel?.AimAmount ?? 0f, Breath = BreathView, Scope = ScopeView, Spread = CrosshairSpread };
        AddChild(_hud); // the hotbar names are set at each spawn (ApplySpawnLoadout)
        _hud.TacticalMapTeleport = OnMapTeleport;
        _hud.SetSandbox(SandboxGranted, SandboxHeldStreak, SandboxTakesDamage); // the SANDBOX badge (#156)
        WireLoadoutHud(_hud);
        _hud.AttachEquipment(this); // F (#268)
        _hud.AttachHuntersLog(HuntLogEnvironment()); // J (#336)
        HuntData.RequestLog(0); // learns the player's own id in the log (their own announcements say "you"), and warms My Log
        _hud.Minimap.Build(Map); // once per connection; the texture comes from the map's parcels and roads (ticket #119)
        _hud.PrewarmTacticalMap(GetViewport().GetVisibleRect().Size); // the full-screen map's picture is made on a worker meanwhile
    }

    /// <summary>The loaded map (set by the host): the minimap's texture source.</summary>
    public Drycastle.MapLoader.MapLoader? Map;
    readonly List<Vector2> _carDots = new();

    static string WeaponName(string id)
    {
        var k = "weapon." + id;
        var t = Strings.T(k);
        return t == k ? id : t;
    }

    /// <summary>Ammo of the hotbar: the selected hotbar cell, and every hotbar weapon's magazine in hotbar order.</summary>
    void HudAmmo()
    {
        if (_hud == null || Ammo is not { } a || Weapons == null || a.Slot < 1 || a.Slot > Weapons.Weapons.Count) return;
        var w = Weapons.Weapons[a.Slot - 1];
        var all = new List<(int, int, bool)>();
        foreach (var gi in _hotbar)
            if (gi < a.Slots.Count) all.Add((a.Slots[gi].Mag, a.Slots[gi].Reserve, Weapons.Weapons[gi].MagazineSize == 0));
        var (mag, reserve) = a.Slots[a.Slot - 1];
        _hud.SetAmmo(Math.Max(1, HotbarPos(a.Slot) + 1), w.MagazineSize == 0 ? -1 : mag, reserve, all, a.ReloadRemaining, w.ReloadTime);
    }

    HuntArtResolver? _huntArt;

    /// <summary>What the hit diagram and the Hunter's Log draw an animal from (shapes, baked pictures, plain bodies).</summary>
    HuntArtResolver HuntArtOf => _huntArt ??= new HuntArtResolver(AnimalShapes, AnimalData);

    /// <summary>The hit diagram of an animal this player just killed with a sniper rifle.</summary>
    void HudHuntShot(HuntShotMsg m)
    {
        if (_hud == null || m.Species >= AnimalData.Animals.Count) return;
        var def = AnimalData.Animals[m.Species];
        var weapon = Weapons != null && m.Weapon < Weapons.Weapons.Count ? WeaponName(Weapons.Weapons[m.Weapon].Id) : "";
        // always the look's one standing picture, whatever the animal was doing: the hit is moved from the shape the server tested the bullet
        // against (the pose it was in) onto the standing shape, head to head and body to body. Without shapes: the species' baked picture
        var key = def.ShapeKey(m.Variant);
        var standing = AnimalShapes.Frame(key, AnimalPose.Idle, 0);
        var (along, height) = (m.Along, m.Height);
        if (standing != null && m.Pose != HuntShot.NoShape && AnimalShapes.Frame(key, (AnimalPose)m.Pose, m.Frame) is { } hitOn)
            (along, height) = hitOn.MapOnto(standing, m.Along, m.Height, m.Head);
        else if (m.Pose == HuntShot.NoShape) standing = null; // tested against the body box: its picture, by the box
        var line = Strings.F(m.Head ? "hud.hunt.line.head" : "hud.hunt.line", m.DistanceM, weapon);
        // the species and what it weighed (a species with no weight: the name alone)
        var name = def.Name.ToUpperInvariant();
        var title = m.WeightKg > 0 ? Strings.F("hud.hunt.title", name, m.WeightKg.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) : name;
        _hud.ShowHuntShot(title, HuntArtOf.Art(standing, def.Id), along, height, line);
    }

    void HudHit(HitMsg h)
    {
        // a gunner's hit marker is the turret overlay's while the HUD is hidden (ticket #152); the hit event reaches it either way
        if (h.Attacker == _myId && h.Victim != _myId && _gunship != null) _gunOverlay.Hit(h.Headshot, h.Kill);
        if (_hud == null) return;
        if (h.Attacker == _myId && h.Victim != _myId) _hud.Hit(h.Headshot, h.Kill);
        if (h.Victim == _myId && h.Attacker != _myId && _me != null && _remotes.TryGetValue(h.Attacker, out var r))
        {
            var me = _me.GlobalPosition; var src = r.Node.GlobalPosition;
            _hud.Damage(HudLogic.DamageAngle(me.X, me.Z, _me.Yaw, src.X, src.Z));
        }
        else if (h.Victim == _myId && GangRules.IsHitId(h.Attacker) && _me != null && NearestGangMember(_me.GlobalPosition) is { } member)
            _hud.Damage(HudLogic.DamageAngle(_me.GlobalPosition.X, _me.GlobalPosition.Z, _me.Yaw, member.X, member.Z)); // a blow of a Gang member: the marker points at the nearest one
        else if (h.Victim == _myId && RaidVanRules.IsHitId(h.Attacker) && _me != null && NearestVan(_me.GlobalPosition) is { } van)
            _hud.Damage(HudLogic.DamageAngle(_me.GlobalPosition.X, _me.GlobalPosition.Z, _me.Yaw, van.X, van.Z)); // a Raid van ran into the player: the marker points at the nearest van
        else if (h.Victim == _myId && h.Attacker == BigfootRules.TargetId && _me != null && _bigfoots.Values.FirstOrDefault() is { } giant)
        {
            var me = _me.GlobalPosition; var src = giant.Node.GlobalPosition; // the Bigfoot's logs, shockwave and launch: the marker points at it (#264)
            _hud.Damage(HudLogic.DamageAngle(me.X, me.Z, _me.Yaw, src.X, src.Z));
        }
    }

    readonly List<Vector2> _friendDots = new();

    /// <summary>Anyone but me who is not a teammate (FFA: everyone else).</summary>
    bool IsMapEnemy(ushort id) => id != _myId && !Match.AreFriends(_myId, id);

    void UpdateHud()
    {
        _hud?.SetControlMode(Mode.RemoteViewOwnsScreen); // one mode switch: a turret or the missile owns the screen (ticket #152)
        _hud?.SetHealth(Mathf.CeilToInt(_me?.Health ?? 100));
        _hud?.CoverCrosshair(_chooser.IsOpen);
        _hud?.SetUnarmedSeat(InUnarmedSeat); // no crosshair, no ammo readout where nothing can be fired (#373)
        if (_me != null) _me.MoveWithFreeMouse = (_hud?.EquipmentOpen ?? false) || (_hud?.TacticalMapOpen ?? false) || (_hud?.HuntersLogOpen ?? false); // the Equipment screen, the tactical map and the Hunter's Log free the mouse, not the legs (#268, #336)
        if (_hud is { EquipmentOpen: true } or { TacticalMapOpen: true } or { HuntersLogOpen: true })
        {
            var overlays = _hud.Overlays(MenuOpen?.Invoke() == true, ChooserOpen); // a menu or the corpse chooser takes the screen from the Equipment screen, a menu from the map
            if (_hud.EquipmentOpen && OverlayArbiter.YieldsToMenu(Overlay.Equipment, overlays)) _hud.CloseEquipment();
            if (_hud.TacticalMapOpen && OverlayArbiter.YieldsToMenu(Overlay.TacticalMap, overlays)) _hud.CloseTacticalMap();
            if (_hud.HuntersLogOpen && OverlayArbiter.YieldsToMenu(Overlay.HuntersLog, overlays)) _hud.CloseHuntersLog();
        }
        UavTick();
        if (_me != null) RozhlasFeedPlayer.Current?.SetListener(_me.GlobalPosition);
        if (_me != null) Rozhlas.Current?.SetListener(_me.GlobalPosition); // the loudspeaker is heard where the player is, not where a vehicle's orbiting camera is
        if (_me != null && _hud != null)
        {
            var g = _me.GlobalPosition;
            var src = MinimapView.Select(missilePov: _povCam is { Current: true }, turret: _gunship != null, driving: _driving);
            _hud.Minimap.SetPlayer(new Vector2(g.X, g.Z), MinimapView.YawFor(src, _me.Yaw, _carYaw, _turretViewYaw, _missileViewYaw));
            _carDots.Clear();
            foreach (var c in _cars.Values)
            {
                if (c.Destroyed) continue;
                _seatOccupants.TryGetValue(c.Id, out var others); // an enemy on any seat: not on the map (#313)
                if (HudLogic.HideEnemyOccupiedVehicle(c.Driver, others, _myId, IsMapEnemy)) continue;
                if (Vehicles != null && c.Kind < Vehicles.Vehicles.Count && ScudTruckHidden(Vehicles.Vehicles[c.Kind])) continue; // the Scud event's truck is not on any map until it is found
                _carDots.Add(new Vector2(c.Node.GlobalPosition.X, c.Node.GlobalPosition.Z));
            }
            _hud.Minimap.SetVehicleDots(_carDots);
            _friendDots.Clear();
            if (Match.IsTeamMode)
                foreach (var (id, r) in _remotes)
                    if (r.State != EntityState.Dead && Match.AreFriends(_myId, id) && GodotObject.IsInstanceValid(r.Node))
                        _friendDots.Add(new Vector2(r.Node.GlobalPosition.X, r.Node.GlobalPosition.Z));
            _hud.Minimap.SetFriendDots(_friendDots);
        }
    }
}

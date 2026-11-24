using Godot;
using Drycastle.Shooter.Loudspeaker;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Client;

/// <summary>Feeds the in-match <see cref="Hud"/> from client state: events push changes (the ammo, the hits), the per-frame hook forwards the rest.</summary>
internal sealed class HudClient
{
    readonly ClientContext _ctx;
    readonly OwnWeapon _weapon;
    readonly DriveClient _drive;
    readonly AircraftClient _aircraft;
    readonly ThievesClient _thieves;
    readonly BigfootClient _bigfoot;
    readonly ScreensClient _screens;
    readonly UavClient _uav;
    readonly MinimapFeed _minimap;
    readonly InteractTargets _targets;
    readonly Func<int> _snapshots;

    /// <param name="snapshots">How many snapshots arrived (the FPS card's TPS line).</param>
    public HudClient(ClientContext ctx, OwnWeapon weapon, DriveClient drive, AircraftClient aircraft, ThievesClient thieves, BigfootClient bigfoot, ScreensClient screens, UavClient uav, MinimapFeed minimap, InteractTargets targets, Func<int> snapshots)
    {
        _ctx = ctx; _weapon = weapon; _drive = drive; _aircraft = aircraft; _thieves = thieves; _bigfoot = bigfoot; _screens = screens; _uav = uav; _minimap = minimap; _targets = targets; _snapshots = snapshots;
    }

    /// <summary>Spread the next shot would get, for the crosshair gap: the same rule as the server's shot (stance, aim, movement).</summary>
    public float CrosshairSpread()
    {
        if (_ctx.CurrentWeapon is not { } w) return 0f;
        var own = HitMath.OwnSpread(w, _weapon.Spread.Degrees(w, _ctx.Now));
        // in an armed seat the vehicle's speed is what widens it (the server's rule for a shot from the seat)
        if (_ctx.View.InArmedSeat && _ctx.Cars.MySeat() is { } m) return SeatAimRules.ShotSpread(own, w, _weapon.AimHeld, SeatsClient.RideSpeed(m.Car, _drive.StateOf(m.Car)), m.Seat);
        return _ctx.Me is { } me ? HitMath.ShotSpread(own, w, me.State, _weapon.AimHeld) : own;
    }

    /// <summary>Ammo of the hotbar: the selected hotbar cell, and every hotbar weapon's magazine in hotbar order.</summary>
    public void Ammo()
    {
        if (_ctx.Hud == null || _ctx.Ammo is not { } a || _ctx.Weapons == null || a.Slot < 1 || a.Slot > _ctx.Weapons.Weapons.Count) return;
        var w = _ctx.Weapons.Weapons[a.Slot - 1];
        var all = new List<(int, int, bool)>();
        foreach (var gi in _ctx.Hotbar.Slots)
            if (gi < a.Slots.Count) all.Add((a.Slots[gi].Mag, a.Slots[gi].Reserve, _ctx.Weapons.Weapons[gi].MagazineSize == 0));
        var (mag, reserve) = a.Slots[a.Slot - 1];
        _ctx.Hud.SetAmmo(Math.Max(1, _ctx.Hotbar.PosOf(a.Slot) + 1), w.MagazineSize == 0 ? -1 : mag, reserve, all, a.ReloadRemaining, w.ReloadTime);
    }

    public void OnHit(HitMsg h)
    {
        // a gunner's hit marker is the turret overlay's while the HUD is hidden (ticket #152); the hit event reaches it either way
        if (h.Attacker == _ctx.MyId && h.Victim != _ctx.MyId) _aircraft.HitMarker(h.Headshot, h.Kill);
        if (_ctx.Hud == null) return;
        if (h.Attacker == _ctx.MyId && h.Victim != _ctx.MyId) _ctx.Hud.Hit(h.Headshot, h.Kill);
        if (h.Victim != _ctx.MyId || _ctx.Me == null) return;
        // the chevron follows its source while it shows (#424); a source that is gone leaves it at its last angle
        if (h.Attacker != _ctx.MyId && _ctx.Remotes.TryGetValue(h.Attacker, out var r)) DamageFrom(h.Attacker, () => NodePos(r.Node));
        else if (GangRules.IsHitId(h.Attacker)) DamageFrom(h.Attacker, () => _thieves.NearestGangMember(_ctx.Me.GlobalPosition)); // a blow of a Gang member: the marker points at the nearest one
        else if (RaidVanRules.IsHitId(h.Attacker)) DamageFrom(h.Attacker, () => _thieves.NearestVan(_ctx.Me.GlobalPosition)); // a Raid van ran into the player: the marker points at the nearest van
        else if (h.Attacker == BigfootRules.TargetId) DamageFrom(h.Attacker, () => _bigfoot.FirstNode is { } giant ? NodePos(giant) : null); // the Bigfoot's logs, shockwave and launch: the marker points at it (#264)
    }

    static Vector3? NodePos(Node3D n) => GodotObject.IsInstanceValid(n) && n.IsInsideTree() ? n.GlobalPosition : null;

    void DamageFrom(object key, Func<Vector3?> source) => _ctx.Hud!.Damage(key, () =>
    {
        if (_ctx.Me == null || !GodotObject.IsInstanceValid(_ctx.Me) || source() is not { } s) return null;
        var m = _ctx.Me.GlobalPosition;
        return HudLogic.DamageAngle(m.X, m.Z, _ctx.Me.Yaw, s.X, s.Z);
    });

    public void Update()
    {
        _ctx.Hud?.SetControlMode(_ctx.View.Mode.RemoteViewOwnsScreen); // one mode switch: a turret or the missile owns the screen (ticket #152)
        _ctx.Hud?.SetHealth(Mathf.CeilToInt(_ctx.Me?.Health ?? 100));
        _ctx.Hud?.SetSnapshotCount(_snapshots()); // the FPS card's TPS line (Hud.Fps.cs)
        _ctx.Hud?.CoverCrosshair(_targets.ChooserOpen());
        _ctx.Hud?.SetUnarmedSeat(_ctx.View.InUnarmedSeat); // no crosshair, no ammo readout where nothing can be fired (#373)
        _screens.Update();
        _uav.Tick();
        if (_ctx.Me != null) RozhlasFeedPlayer.Current?.SetListener(_ctx.Me.GlobalPosition);
        if (_ctx.Me != null) Rozhlas.Current?.SetListener(_ctx.Me.GlobalPosition); // the loudspeaker is heard where the player is, not where a vehicle's orbiting camera is
        _minimap.Update();
    }
}

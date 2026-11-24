using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Flight;
using Drycastle.Shooter.HeadsUp.Streaks;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client.Shooting;
using Drycastle.Shooter.Net.Client.Streaks;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Flight;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.Aviation;

/// <summary>
/// Aircraft on the client (ticket #125): each aircraft world entity is drawn interpolated like the other entities, with its
/// rotors turning and its engine sound. An aircraft owned by the local player is their gunship: the camera moves to the
/// turret (<see cref="Player.Turret"/>), the view is clamped to the turret's limits, the body is not simulated, and the
/// <see cref="GunshipOverlay"/> shows the thermal grade and the weapon. The server resolves the shots; the status it sends
/// (<see cref="EventKind.GunshipStatus"/>) is what the overlay shows.
/// </summary>
internal sealed class AircraftClient
{
    readonly ClientContext _ctx;
    readonly KillstreaksClient _streaks;
    readonly BlastDustClient _blastDust;
    readonly EnemyChestPoints _markers;
    readonly Func<Vector3, bool> _homingKillShown;

    /// <param name="homingKillShown">A homing missile's kill blast is already drawn at this place.</param>
    public AircraftClient(ClientContext ctx, KillstreaksClient streaks, BlastDustClient blastDust, EnemyChestPoints markers, Func<Vector3, bool> homingKillShown)
    {
        _ctx = ctx; _streaks = streaks; _blastDust = blastDust; _markers = markers; _homingKillShown = homingKillShown;
        // follows aircraft entities and the owner's status events, builds the overlay
        ctx.WorldEntities.Spawned += OnSpawned;
        ctx.WorldEntities.Despawned += OnDespawned;
        ctx.WorldEntities.DespawnedFor += OnDespawnedFor;
        _gunOverlay = new GunshipOverlay();
        ctx.Root.AddChild(_gunOverlay);
    }

    /// <summary>The local player steers their gunship's turret.</summary>
    public bool AtTurret => _gunship != null;

    /// <summary>The turret camera's yaw this frame (the minimap follows it, ticket #150).</summary>
    public float TurretViewYaw => _turretViewYaw;

    /// <summary>The hint line of the turret overlay changed: redraw it at the next frame.</summary>
    public void MarkDirty() => _gunDirty = true;

    /// <summary>The owner's hit marker while at the turret.</summary>
    public void HitMarker(bool headshot, bool kill) { if (_gunship != null) _gunOverlay.Hit(headshot, kill); }

    /// <summary>Where the aircraft <paramref name="id"/> is drawn; null when there is no such aircraft.</summary>
    public Vector3? PositionOf(ushort id) => _aircraft.TryGetValue(id, out var view) ? view.Model.GlobalPosition : null;

    /// <summary>The ids of the aircraft in the world.</summary>
    public IEnumerable<ushort> Ids => _aircraft.Keys;

    sealed class AircraftView
    {
        public AircraftModel Model = null!;
        public RotorSound? Sound;
        /// <summary>Height above the ground under the circle centre (the aim's ground plane is the model's Y minus this).</summary>
        public float AltitudeM;
    }

    readonly Dictionary<ushort, AircraftView> _aircraft = new();
    /// <summary>The local player's gunship while it exists (null otherwise).</summary>
    AircraftView? _gunship;
    /// <summary>
    /// The turret's look, world yaw and pitch (#160): the mouse moves it, zoom-scaled. It changes only when the mouse does. The
    /// aim the server gets is <see cref="_aimPoint"/> (#161), the point this look meets the ground at, not these angles.
    /// </summary>
    float _lookYaw, _lookPitch;
    /// <summary>The raw look (the player's mouse yaw and pitch) last seen, so this frame's mouse move is the difference from it.</summary>
    float _rawYaw, _rawPitch;
    /// <summary>The ground point the turret is on: fixed while the mouse is still, re-anchored when it moves.</summary>
    System.Numerics.Vector3 _aimPoint;
    /// <summary>The yaw the turret camera was set to this frame, unwrapped so it never jumps at +-pi (the minimap follows it, ticket #150).</summary>
    float _turretViewYaw;
    GunshipStatus _gunStatus;
    bool _gunDirty;
    GunshipOverlay _gunOverlay = null!;
    HelperWarningOverlay? _helperWarn;


    void OnSpawned(WorldReplica.Entity e)
    {
        if (e.Type != Aircraft.WireType) return;
        var kinds = _ctx.Killstreaks?.AircraftKinds;
        var index = AircraftState.Unpack(e.State.State).Kind;
        var kindId = kinds != null && index < kinds.Count ? kinds[index].Id : "helicopter";
        var model = AircraftModel.Build(kindId);
        _ctx.Owned.AddChild(model);
        var view = new AircraftView { Model = model, AltitudeM = kinds != null && index < kinds.Count ? kinds[index].AltitudeM : 0f };
        if (ShotFx.Enabled && kindId != "uav") // the UAV is silent for now (an engine loop is optional in #303)
        {
            view.Sound = new RotorSound { Level = kindId == "gunship" ? 0.7f : 0.5f, BladeHz = kindId == "gunship" ? 24f : 36f };
            model.AddChild(view.Sound);
        }
        _aircraft[e.Id] = view;
    }

    void OnDespawned(ushort id)
    {
        if (!_aircraft.Remove(id, out var v)) return;
        if (_gunship == v) EndGunship();
        v.Model.QueueFree();
    }

    /// <summary>A shot-down aircraft blows up where it flew (an expired or removed one just leaves); runs before <see cref="OnDespawned"/> frees the model.</summary>
    void OnDespawnedFor(ushort id, WorldDespawnReason reason)
    {
        if (reason != WorldDespawnReason.Destroyed || !_aircraft.TryGetValue(id, out var v)) return;
        var at = v.Model.GlobalPosition;
        if (_homingKillShown(at)) return; // a homing missile's kill blast (the Explosion event, with its Blast wave) is already there
        RocketFx.Explode(_ctx.Owned, at, AircraftCrashRadius);
        _blastDust.BlastFelt(at, AircraftCrashRadius, "explosion.vehicle");
    }

    /// <summary>The size of the blast of a shot-down aircraft (the vehicle blast is 9 m).</summary>
    const float AircraftCrashRadius = 12f;

    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.GunshipStatus || GunshipCodec.Decode(payload) is not { } s) return;
        _gunStatus = s;
        _gunDirty = true;
    }

    /// <summary>Places the aircraft, spins their rotors, and finds the local player's gunship (the one they own).</summary>
    public void Render(double delta)
    {
        if (_ctx.LastTick == 0) return;
        var rt = _ctx.RenderTick;
        ushort mineId = 0;
        foreach (var (id, v) in _aircraft)
        {
            if (!_ctx.WorldEntities.Entities.TryGetValue(id, out var e)) continue;
            v.Model.Spin((float)delta);
            if (e.Buf.At(rt) is { } s)
            {
                var p = s.Pos;
                var (_, bank) = AircraftState.Unpack(e.State.State);
                v.Model.GlobalPosition = new Vector3(p.X, p.Y, p.Z);
                v.Model.Rotation = new Vector3(0, s.Yaw, -bank); // roll: positive bank is the right wing down
            }
            if (_ctx.MyId != 0 && e.State.Owner == _ctx.MyId) mineId = id;
        }

        if (mineId != 0 && _aircraft.TryGetValue(mineId, out var mine) && !_ctx.Dead && _ctx.Me != null && _streaks.Gunner.GunDef() is { } gun)
        {
            var mount = mine.Model.GlobalPosition - Vector3.Up * GunshipControl.TurretDropM;
            var groundY = mine.Model.GlobalPosition.Y - mine.AltitudeM;
            var maxD = GunshipAim.MaxDistance(gun);
            var starting = _gunship != mine;
            if (starting)
            {
                // the turret takes the look the player had, anchored to the ground point it is on (ground-stabilised, #160)
                _gunship = mine; _gunDirty = true;
                _lookYaw = _ctx.Me.Yaw; _lookPitch = GunshipAim.ClampPitch(_ctx.Me.Pitch, gun);
                _rawYaw = _ctx.Me.Yaw; _rawPitch = _ctx.Me.Pitch;
                _aimPoint = GunshipAim.AimPoint(ToNum(mount), _lookYaw, _lookPitch, groundY, maxD);
                _ctx.Me.TurretLook = true;
            }
            GunAimStep(mount, groundY, maxD, gun);
            var (yaw, pitch) = GunshipAim.View(ToNum(mount), _aimPoint, gun);
            _turretViewYaw = starting ? yaw : GunshipAim.Unwrap(_turretViewYaw, yaw);
            _ctx.Me.Turret(mount, _turretViewYaw, pitch);
            _ctx.Me.ZoomFov = GameSettings.Current.Fov / (_gunStatus.Zoom < gun.ZoomLevels.Count ? gun.ZoomLevels[_gunStatus.Zoom] : 1f);
            if (_gunDirty) { _gunOverlay.Present(_gunStatus, gun, _streaks.Gunner.HeliCycleWeapons && !_streaks.Gunner.Ac130Active, _streaks.Gunner.Ac130Active); _gunDirty = false; }
            // the timed streak's clock: the helicopter's, or the AC-130's time left (whole seconds, its kills are not counted here)
            var (timer, kills) = _streaks.Gunner.Ac130Active ? (Strings.F("ac130.timer", (int)Math.Ceiling(_streaks.Gunner.Ac130SecondsLeft)), "") : _streaks.Gunner.HeliClock();
            _gunOverlay.SetClock(timer, kills);
            _gunOverlay.SetMarkers(_ctx.Me.Camera, _markers.Points());
        }
        else if (_gunship != null) EndGunship();

        _ctx.Viewmodel.Visible = _gunship == null;
    }

    /// <summary>Back to the body's first-person view, looking where the turret pointed.</summary>
    void EndGunship()
    {
        _gunship = null;
        _gunDirty = false;
        _gunOverlay.Dismiss();
        if (_ctx.Me != null) { _ctx.Me.TurretLook = false; _ctx.Me.EndChase(_turretViewYaw); } // back to the body, looking where the turret looked
    }

    static System.Numerics.Vector3 ToNum(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The magnification of the turret's zoom step now (1 when out of range).</summary>
    float MagnificationOf(GunshipDef gun) => _gunStatus.Zoom < gun.ZoomLevels.Count ? gun.ZoomLevels[_gunStatus.Zoom] : 1f;

    /// <summary>
    /// Moves the turret's look by this frame's mouse move (the raw look's change since last frame), scaled down by the zoom, and
    /// re-anchors the aim point to the ground there. The move is applied to the view as it is on screen (the aim point seen from
    /// the turret now), so the reticle moves from where it is. With no mouse move the aim point stays where it is (#160).
    /// </summary>
    void GunAimStep(Vector3 mount, float groundY, float maxDistance, GunshipDef gun)
    {
        var dYaw = GunshipAim.Wrap(_ctx.Me!.Yaw - _rawYaw);
        var dPitch = _ctx.Me.Pitch - _rawPitch;
        _rawYaw = _ctx.Me.Yaw; _rawPitch = _ctx.Me.Pitch;
        if (dYaw == 0 && dPitch == 0) return;
        var (viewYaw, viewPitch) = GunshipAim.View(ToNum(mount), _aimPoint, gun);
        (_lookYaw, _lookPitch) = GunshipAim.Turn(viewYaw, viewPitch, dYaw, dPitch, MagnificationOf(gun), gun);
        _aimPoint = GunshipAim.AimPoint(ToNum(mount), _lookYaw, _lookPitch, groundY, maxDistance);
    }

    /// <summary>
    /// At the turret the input carries the turret's look, not the raw mouse look, which stays the player's own. Returns the pitch to
    /// send. The aim point (#161) goes with it: <see cref="GunnerAim"/> is what the server fires at. Called on the input tick.
    /// </summary>
    public float GunnerLook(ref MoveInput mi)
    {
        if (_gunship == null || _ctx.Me == null) return _ctx.Me?.Pitch ?? 0f;
        mi.Yaw = _lookYaw;
        return _lookPitch;
    }

    /// <summary>
    /// The aim the input carries while steering a turret (#161): the ground point the reticle is on, from this client's own muzzle.
    /// The server fires from its later muzzle toward this point, so the rounds land where the reticle is. Null when not at a turret.
    /// </summary>
    public System.Numerics.Vector3? GunnerAim => _gunship != null && _ctx.Me != null ? _aimPoint : null;

    /// <summary>EventKind.HelperWarning, sent to this player only: the red dot and cue for the helicopter's one-second warning.</summary>
    public void OnHelperWarning(HelperWarning w)
    {
        if (w.Target != _ctx.MyId) return;
        if (Sfx.Enabled) Sfx.Play2D("helper.warning");
        _helperWarn ??= new HelperWarningOverlay();
        if (!_helperWarn.IsInsideTree()) _ctx.Root.AddChild(_helperWarn);
        var helicopter = w.Helicopter;
        _helperWarn.Show(() => _aircraft.TryGetValue(helicopter, out var view) ? view.Model.GlobalPosition : null, w.Seconds);
    }
}

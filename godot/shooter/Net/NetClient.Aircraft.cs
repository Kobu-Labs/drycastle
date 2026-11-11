using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Aircraft on the client (ticket #125): each aircraft world entity is drawn interpolated like the other entities, with its
/// rotors turning and its engine sound. An aircraft owned by the local player is their gunship: the camera moves to the
/// turret (<see cref="Player.Turret"/>), the view is clamped to the turret's limits, the body is not simulated, and the
/// <see cref="GunshipOverlay"/> shows the thermal grade and the weapon. The server resolves the shots; the status it sends
/// (<see cref="EventKind.GunshipStatus"/>) is what the overlay shows.
/// </summary>
public partial class NetClient
{
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


    /// <summary>Called once from <see cref="_Ready"/>: follows aircraft entities, the owner's status events and builds the overlay.</summary>
    void InitAircraft()
    {
        WorldEntities.Spawned += OnAircraftSpawned;
        WorldEntities.Despawned += OnAircraftDespawned;
        WorldEntities.DespawnedFor += OnAircraftDespawnedFor;
        EventReceived += OnAircraftEvent;
        _gunOverlay = new GunshipOverlay();
        AddChild(_gunOverlay);
    }

    void OnAircraftSpawned(WorldReplica.Entity e)
    {
        if (e.Type != Aircraft.WireType) return;
        var kinds = Killstreaks?.AircraftKinds;
        var index = AircraftState.Unpack(e.State.State).Kind;
        var kindId = kinds != null && index < kinds.Count ? kinds[index].Id : "helicopter";
        var model = AircraftModel.Build(kindId);
        Owned.AddChild(model);
        var view = new AircraftView { Model = model, AltitudeM = kinds != null && index < kinds.Count ? kinds[index].AltitudeM : 0f };
        if (ShotFx.Enabled)
        {
            view.Sound = new RotorSound { Level = kindId == "gunship" ? 0.7f : 0.5f, BladeHz = kindId == "gunship" ? 24f : 36f };
            model.AddChild(view.Sound);
        }
        _aircraft[e.Id] = view;
    }

    void OnAircraftDespawned(ushort id)
    {
        if (!_aircraft.Remove(id, out var v)) return;
        if (_gunship == v) EndGunship();
        v.Model.QueueFree();
    }

    /// <summary>A shot-down aircraft blows up where it flew (an expired or removed one just leaves); runs before <see cref="OnAircraftDespawned"/> frees the model.</summary>
    void OnAircraftDespawnedFor(ushort id, WorldDespawnReason reason)
    {
        if (reason != WorldDespawnReason.Destroyed || !_aircraft.TryGetValue(id, out var v)) return;
        var at = v.Model.GlobalPosition;
        RocketFx.Explode(Owned, at, AircraftCrashRadius);
        if (Sfx.Enabled) Sfx.Play3D("explosion.vehicle", at);
        ShakeFor(at, AircraftCrashRadius);
    }

    /// <summary>The size of the blast of a shot-down aircraft (the vehicle blast is 9 m).</summary>
    const float AircraftCrashRadius = 12f;

    void OnAircraftEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.GunshipStatus || GunshipCodec.Decode(payload) is not { } s) return;
        _gunStatus = s;
        _gunDirty = true;
    }

    /// <summary>Places the aircraft, spins their rotors, and finds the local player's gunship (the one they own).</summary>
    void RenderAircraft(double delta)
    {
        if (_lastTick == 0) return;
        var rt = _lastTick + (Time.GetTicksMsec() / 1000.0 - _lastArrival) * Wire.TickRate - InterpDelayTicks;
        ushort mineId = 0;
        foreach (var (id, v) in _aircraft)
        {
            if (!WorldEntities.Entities.TryGetValue(id, out var e)) continue;
            v.Model.Spin((float)delta);
            if (e.Buf.At(rt) is { } s)
            {
                var p = s.Pos;
                var (_, bank) = AircraftState.Unpack(e.State.State);
                v.Model.GlobalPosition = new Vector3(p.X, p.Y, p.Z);
                v.Model.Rotation = new Vector3(0, s.Yaw, -bank); // roll: positive bank is the right wing down
            }
            if (_myId != 0 && e.State.Owner == _myId) mineId = id;
        }

        if (mineId != 0 && _aircraft.TryGetValue(mineId, out var mine) && !Dead && _me != null && GunDef() is { } gun)
        {
            var mount = mine.Model.GlobalPosition - Vector3.Up * GunshipControl.TurretDropM;
            var groundY = mine.Model.GlobalPosition.Y - mine.AltitudeM;
            var maxD = GunshipAim.MaxDistance(gun);
            var starting = _gunship != mine;
            if (starting)
            {
                // the turret takes the look the player had, anchored to the ground point it is on (ground-stabilised, #160)
                _gunship = mine; _gunDirty = true;
                _lookYaw = _me.Yaw; _lookPitch = GunshipAim.ClampPitch(_me.Pitch, gun);
                _rawYaw = _me.Yaw; _rawPitch = _me.Pitch;
                _aimPoint = GunshipAim.AimPoint(ToNum(mount), _lookYaw, _lookPitch, groundY, maxD);
                _me.TurretLook = true;
            }
            GunAimStep(mount, groundY, maxD, gun);
            var (yaw, pitch) = GunshipAim.View(ToNum(mount), _aimPoint, gun);
            _turretViewYaw = starting ? yaw : GunshipAim.Unwrap(_turretViewYaw, yaw);
            _me.Turret(mount, _turretViewYaw, pitch);
            _me.ZoomFov = GameSettings.Current.Fov / (_gunStatus.Zoom < gun.ZoomLevels.Count ? gun.ZoomLevels[_gunStatus.Zoom] : 1f);
            if (_gunDirty) { _gunOverlay.Present(_gunStatus, gun, HeliCycleWeapons && !_ac130Active, _ac130Active); _gunDirty = false; }
            // the timed streak's clock: the helicopter's, or the AC-130's time left (whole seconds, its kills are not counted here)
            var (timer, kills) = _ac130Active ? (Strings.F("ac130.timer", (int)Math.Ceiling(Ac130SecondsLeft)), "") : HeliClock();
            _gunOverlay.SetClock(timer, kills);
            _gunOverlay.SetMarkers(_me.Camera, EnemyMarkerPoints());
        }
        else if (_gunship != null) EndGunship();

        if (_viewmodel != null) _viewmodel.Visible = _gunship == null;
    }

    /// <summary>The enemy markers' points (ticket #157): the chest of every live enemy player and target dummy. Reused each frame, no allocation.</summary>
    readonly List<Vector3> _enemyMarkers = new();

    /// <summary>
    /// Fills <see cref="_enemyMarkers"/> from the remote replicas (players and target dummies share them): every one but the owner
    /// and the dead (a dummy's Defeat pose, a player's body). Positions are the replicas' feet plus the chest height.
    /// </summary>
    List<Vector3> EnemyMarkerPoints()
    {
        _enemyMarkers.Clear();
        foreach (var (id, r) in _remotes)
        {
            if (id == _myId || r.State == EntityState.Dead || !GodotObject.IsInstanceValid(r.Node)) continue;
            _enemyMarkers.Add(r.Node.GlobalPosition + Vector3.Up * 1.0f);
        }
        return _enemyMarkers;
    }

    /// <summary>Back to the body's first-person view, looking where the turret pointed.</summary>
    void EndGunship()
    {
        _gunship = null;
        _gunDirty = false;
        _gunOverlay.Dismiss();
        if (_me != null) { _me.TurretLook = false; _me.EndChase(_turretViewYaw); } // back to the body, looking where the turret looked
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
        var dYaw = GunshipAim.Wrap(_me!.Yaw - _rawYaw);
        var dPitch = _me.Pitch - _rawPitch;
        _rawYaw = _me.Yaw; _rawPitch = _me.Pitch;
        if (dYaw == 0 && dPitch == 0) return;
        var (viewYaw, viewPitch) = GunshipAim.View(ToNum(mount), _aimPoint, gun);
        (_lookYaw, _lookPitch) = GunshipAim.Turn(viewYaw, viewPitch, dYaw, dPitch, MagnificationOf(gun), gun);
        _aimPoint = GunshipAim.AimPoint(ToNum(mount), _lookYaw, _lookPitch, groundY, maxDistance);
    }

    /// <summary>
    /// At the turret the input carries the turret's look, not the raw mouse look, which stays the player's own. Returns the pitch to
    /// send. The aim point (#161) goes with it: <see cref="GunnerAim"/> is what the server fires at. Called on the input tick.
    /// </summary>
    float GunnerLook(ref MoveInput mi)
    {
        if (_gunship == null || _me == null) return _me?.Pitch ?? 0f;
        mi.Yaw = _lookYaw;
        return _lookPitch;
    }

    /// <summary>
    /// The aim the input carries while steering a turret (#161): the ground point the reticle is on, from this client's own muzzle.
    /// The server fires from its later muzzle toward this point, so the rounds land where the reticle is. Null when not at a turret.
    /// </summary>
    System.Numerics.Vector3? GunnerAim => _gunship != null && _me != null ? _aimPoint : null;
}

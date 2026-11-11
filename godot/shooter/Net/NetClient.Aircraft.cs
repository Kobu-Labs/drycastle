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
    }

    readonly Dictionary<ushort, AircraftView> _aircraft = new();
    /// <summary>The local player's gunship while it exists (null otherwise); its yaw is the rendered heading.</summary>
    AircraftView? _gunship;
    float _gunYaw;
    GunshipStatus _gunStatus;
    bool _gunDirty;
    GunshipOverlay _gunOverlay = null!;


    /// <summary>Called once from <see cref="_Ready"/>: follows aircraft entities, the owner's status events and builds the overlay.</summary>
    void InitAircraft()
    {
        WorldEntities.Spawned += OnAircraftSpawned;
        WorldEntities.Despawned += OnAircraftDespawned;
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
        World.AddChild(model);
        var view = new AircraftView { Model = model };
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

        if (mineId != 0 && _aircraft.TryGetValue(mineId, out var mine) && !Dead && _me != null && Killstreaks?.Gunship is { } gun)
        {
            if (_gunship != mine) { _gunship = mine; _gunDirty = true; }
            if (WorldEntities.Entities.TryGetValue(mineId, out var me) && me.Buf.At(rt) is { } ms) _gunYaw = ms.Yaw;
            var (yaw, pitch) = GunshipAim.Clamp(_gunYaw, _me.Yaw, _me.Pitch, gun);
            var mount = mine.Model.GlobalPosition - Vector3.Up * GunshipControl.TurretDropM;
            _me.Turret(mount, yaw, pitch);
            _me.ZoomFov = GameSettings.Current.Fov / (_gunStatus.Zoom < gun.ZoomLevels.Count ? gun.ZoomLevels[_gunStatus.Zoom] : 1f);
            if (_gunDirty) { _gunOverlay.Present(_gunStatus, gun); _gunDirty = false; }
        }
        else if (_gunship != null) EndGunship();

        if (_viewmodel != null) _viewmodel.Visible = _gunship == null;
    }

    /// <summary>Back to the body's first-person view, looking where the turret pointed.</summary>
    void EndGunship()
    {
        _gunship = null;
        _gunDirty = false;
        _gunOverlay.Dismiss();
        _me?.EndChase(_me.Yaw);
    }

    /// <summary>
    /// At the turret the look is clamped to the turret's limits before it is sent, so the view the player sees is the one the
    /// server will apply. Called on the input tick, before the yaw is stored.
    /// </summary>
    void ClampGunnerView(ref MoveInput mi)
    {
        if (_gunship == null || _me == null || Killstreaks?.Gunship is not { } gun) return;
        var (yaw, pitch) = GunshipAim.Clamp(_gunYaw, mi.Yaw, _me.Pitch, gun);
        mi.Yaw = yaw;
        _me.Pitch = pitch;
    }
}

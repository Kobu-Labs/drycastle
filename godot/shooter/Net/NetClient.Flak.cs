using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// The flak truck on the client (ADR 0005). Every client animates the model's turret (yaw), gun cradle (pitch) and barrels (recoil) from the turret
/// record of the snapshot (<see cref="FlakRecord"/>: the world aim and the gunner's id), interpolated like the vehicle. The gunner's own client uses its
/// own look at once, clamped to the barrels' elevation, and puts the camera at the model's sight (<see cref="Player.Turret"/>); the look goes to the
/// server as the usual input yaw and pitch, and Fire and Reload are the usual keys. The tracers, bursts and hit markers are the gunship's coalesced
/// FX (<see cref="OnGunshipShots"/>, <see cref="OnGunshipBlasts"/>). The gunner's ammo is the HUD readout (<see cref="EventKind.FlakStatus"/>).
/// </summary>
public partial class NetClient
{
    /// <summary>The nodes of a flak model that move, found by name (the model's own, see assets/suchohrad-generated-models), and the barrels' recoil.</summary>
    sealed class FlakNodes
    {
        public Node3D Turret = null!, Cradle = null!;
        public Node3D? Sight, BarrelL, BarrelR;
        public Vector3 RestL, RestR;
        public float KickL, KickR;
        int _next;

        /// <summary>Metres a barrel slides back at full kick, and how fast it comes home (metres per second).</summary>
        const float RecoilM = 0.35f, ReturnMps = 2.4f;

        public static FlakNodes? Find(Node3D root)
        {
            if (root.FindChild("turret", true, false) is not Node3D turret || root.FindChild("gun_cradle", true, false) is not Node3D cradle) return null;
            var f = new FlakNodes { Turret = turret, Cradle = cradle, Sight = root.FindChild("sight", true, false) as Node3D };
            f.BarrelL = root.FindChild("barrel_l", true, false) as Node3D;
            f.BarrelR = root.FindChild("barrel_r", true, false) as Node3D;
            if (f.BarrelL != null) f.RestL = f.BarrelL.Position;
            if (f.BarrelR != null) f.RestR = f.BarrelR.Position;
            return f;
        }

        /// <summary>A round left: the next barrel kicks back (the two fire in turn, as on the server).</summary>
        public void Kick()
        {
            if ((_next++ & 1) == 0) KickL = 1f; else KickR = 1f;
        }

        public void Step(float dt)
        {
            KickL = Mathf.MoveToward(KickL, 0, dt * ReturnMps / RecoilM);
            KickR = Mathf.MoveToward(KickR, 0, dt * ReturnMps / RecoilM);
            if (BarrelL != null) BarrelL.Position = RestL + new Vector3(0, 0, KickL * RecoilM);
            if (BarrelR != null) BarrelR.Position = RestR + new Vector3(0, 0, KickR * RecoilM);
        }
    }

    /// <summary>The newest aim of a flak vehicle's turret and its gunner (0 = nobody), by the vehicle's id.</summary>
    sealed class TurretAim
    {
        public readonly Interpolator Buf = new();
        public ushort Gunner;
    }

    readonly Dictionary<ushort, TurretAim> _turretAims = new();
    FlakStatus? _flakStatus;
    bool _flakCamera;

    void InitFlak() => EventReceived += OnFlakEvent;

    void OnFlakEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.FlakStatus && FlakCodec.Decode(payload) is { } s) _flakStatus = s;
    }

    void OnTurretState(EntityRecord rec, uint tick)
    {
        var carId = FlakRecord.CarOf(rec.Id);
        if (!_turretAims.TryGetValue(carId, out var aim)) _turretAims[carId] = aim = new TurretAim();
        aim.Gunner = FlakRecord.Gunner(rec);
        aim.Buf.Push(tick, NVec3.Zero, rec.YawRad, rec.PitchRad);
    }

    /// <summary>The flak vehicle whose gunner is <paramref name="player"/>, or null.</summary>
    CarView? FlakCarOf(ushort player)
    {
        if (player == 0) return null;
        foreach (var (id, aim) in _turretAims)
            if (aim.Gunner == player && _cars.TryGetValue(id, out var v) && v.Flak != null) return v;
        return null;
    }

    /// <summary>
    /// Poses one flak vehicle's turret for this frame. Returns true when the local player is its gunner (the camera is then at its sight and the
    /// ammo readout is on). <paramref name="s"/> is the vehicle's interpolated state, <paramref name="rt"/> the render tick.
    /// </summary>
    bool RenderFlak(CarView v, in Interpolator.Sample s, double rt, float dt)
    {
        var f = v.Flak!;
        var def = Vehicles!.Vehicles[v.Kind].Flak!;
        _turretAims.TryGetValue(v.Id, out var aim);
        var mine = aim != null && aim.Gunner == _myId && _driving && !Dead && _me != null;
        float yaw, pitch;
        if (mine)
        {
            _me!.Pitch = FlakRules.ClampPitch(def, _me.Pitch); // the mouse look is the aim, kept inside the barrels' elevation
            yaw = _me.Yaw; pitch = _me.Pitch;
        }
        else if (aim?.Buf.At(rt) is { } a) { yaw = a.Yaw; pitch = a.Pitch; }
        else { yaw = s.Yaw; pitch = FlakRules.ClampPitch(def, 0); }
        f.Turret.Rotation = new Vector3(0, FlakRules.Wrap(yaw - s.Yaw), 0);
        f.Cradle.Rotation = new Vector3(pitch, 0, 0);
        f.Step(dt);
        if (!mine) return false;
        var body = v.Node.GlobalPosition;
        _me!.GlobalPosition = body;
        _me.Turret(f.Sight?.GlobalPosition ?? body + Vector3.Up * 2.8f, yaw, pitch);
        _flakCamera = true;
        return true;
    }

    /// <summary>The gunner's ammo as the HUD readout: the rounds and the reserve, or a word while it reloads (never colour alone).</summary>
    string FlakReadout() =>
        _flakStatus is not { } s ? Strings.F("hud.flak.ammo", "-", "-")
        : s.ReloadRemaining > 0 ? Strings.T("hud.flak.reload")
        : Strings.F("hud.flak.ammo", s.Magazine, s.Reserve);
}

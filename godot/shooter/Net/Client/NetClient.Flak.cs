using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The flak truck on the client (ADR 0005). Every client animates the model's turret (yaw), gun cradle (pitch) and barrels (recoil) from the turret
/// record of the snapshot (<see cref="FlakRecord"/>: the world aim and the gunner's id), interpolated like the vehicle. The gunner's own client uses its
/// own look at once, clamped to the barrels' elevation, and puts the camera at the model's sight (<see cref="Player.Turret"/>); the look goes to the
/// server as the usual input yaw and pitch, and Fire and Reload are the usual keys. The tracers, bursts and hit markers are the gunship's coalesced
/// FX (<see cref="OnGunshipShots"/>, <see cref="OnGunshipBlasts"/>). The gunner's ammo is the HUD readout (<see cref="EventKind.FlakStatus"/>).
/// </summary>
public partial class NetClient
{

    /// <summary>The newest aim of a flak vehicle's turret and its gunner (0 = nobody), by the vehicle's id.</summary>
    sealed class TurretAim
    {
        public readonly Interpolator Buf = new();
        public ushort Gunner;
    }

    /// <summary>
    /// The gunner's eye in the gun cradle's space (metres, forward is -Z): on the axis between the two barrels (they are 0.27 m either side), 1.55 m
    /// ahead of the trunnion, so in front of the shield (at 0.8 m) and the recoil sleeves (to 1.22 m), and 0.22 m above the barrel axis (the barrels
    /// are 0.09 m thick). The barrels run past both sides of the view and meet the crosshair's line at the muzzles. It is not where the gunner sits:
    /// the model's own <c>sight</c> node is inside the sight box and the shield, which filled the screen with olive.
    /// </summary>
    static readonly Vector3 GunnerEye = new(0f, 0.22f, -1.55f);

    /// <summary>The width of a flak tracer as a multiple of the generic one (3 cm): about 30 cm, a fat streak you can follow.</summary>
    const float FlakTracerWidth = 10f;

    /// <summary>The visual size of an airburst (vehicles.json flak puff_radius): a blast of exactly this radius is a flak burst, drawn as black smoke.</summary>
    float FlakPuffRadius => Vehicles?.Vehicles.Select(v => v.Flak?.PuffRadius ?? 0f).FirstOrDefault(r => r > 0f) ?? 0f;

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
            if (aim.Gunner == player && Ctx.Cars.TryGet(id, out var v) && v.Flak != null) return v;
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
        var mine = aim != null && aim.Gunner == _myId && Ctx.Driving && !Dead && _me != null;
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
        // over the guns: fixed to the cradle behind the breeches and above the barrels, so the barrels run parallel to the view and the crosshair sits just above them
        _me.Turret(f.Cradle.ToGlobal(GunnerEye), yaw, pitch);
        _flakCamera = true;
        return true;
    }

    /// <summary>The gunner's ammo as the HUD readout: the rounds and the reserve, or a word while it reloads (never colour alone).</summary>
    string FlakReadout() =>
        _flakStatus is not { } s ? Strings.F("hud.flak.ammo", "-", "-")
        : s.ReloadRemaining > 0 ? Strings.T("hud.flak.reload")
        : Strings.F("hud.flak.ammo", s.Magazine, s.Reserve);
}

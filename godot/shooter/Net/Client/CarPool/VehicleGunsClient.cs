using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client.Peers;
using Drycastle.Shooter.Net.Client.Seating;
using Drycastle.Shooter.Vehicles;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Casings;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client.CarPool;

/// <summary>
/// The guns a rider works on a vehicle: the flak truck's (ADR 0005) and the utility helicopter's door gun (<see cref="DoorGunRules"/>). Every client poses the model's gun from the turret record of the
/// snapshot (<see cref="FlakRecord"/>: the aim and the gunner's id, interpolated like the vehicle). The gunner's own client uses its own look at once, held inside the gun's arc, and puts the camera at the
/// model's sight (<see cref="Player.Turret"/>); the look goes to the server as the usual input yaw and pitch, and Fire and Reload are the usual keys. The flak's tracers, bursts and hit markers are the
/// gunship's coalesced FX; a door gun's round leaves its muzzle on the model, the barrel kicks and the muzzle flashes, for everyone. The gunner's ammo is the HUD readout (<see cref="EventKind.FlakStatus"/>).
/// </summary>
internal sealed class VehicleGunsClient
{
    readonly ClientContext _ctx;
    readonly VehicleHulls _hulls;
    readonly RiderCamera _rider;
    readonly WaterClient _water;

    public VehicleGunsClient(ClientContext ctx, VehicleHulls hulls, RiderCamera rider, WaterClient water) { _ctx = ctx; _hulls = hulls; _rider = rider; _water = water; }

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
    public const float FlakTracerWidth = 10f;

    /// <summary>The visual size of an airburst (vehicles.json flak puff_radius): a blast of exactly this radius is a flak burst, drawn as black smoke.</summary>
    public float FlakPuffRadius => _ctx.Vehicles?.Vehicles.Select(v => v.Flak?.PuffRadius ?? 0f).FirstOrDefault(r => r > 0f) ?? 0f;

    readonly Dictionary<ushort, TurretAim> _turretAims = new();
    FlakStatus? _flakStatus;

    /// <summary>The gun camera was put on a gunner's sight (a flak or a door gun): it is ended where the look pointed when the gunner leaves.</summary>
    public bool CameraOn;


    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.FlakStatus && FlakCodec.Decode(payload) is { } s) _flakStatus = s;
    }

    public void OnRecord(EntityRecord rec, uint tick)
    {
        var carId = FlakRecord.CarOf(rec.Id);
        if (!_turretAims.TryGetValue(carId, out var aim)) _turretAims[carId] = aim = new TurretAim();
        aim.Gunner = FlakRecord.Gunner(rec);
        aim.Buf.Push(tick, NVec3.Zero, rec.YawRad, rec.PitchRad);
    }

    /// <summary>The flak vehicle whose gunner is <paramref name="player"/>, or null.</summary>
    public CarView? FlakCarOf(ushort player)
    {
        if (player == 0) return null;
        foreach (var (id, aim) in _turretAims)
            if (aim.Gunner == player && _ctx.Cars.TryGet(id, out var v) && v.Flak != null) return v;
        return null;
    }

    /// <summary>
    /// Poses one flak vehicle's turret for this frame. Returns true when the local player is its gunner (the camera is then at its sight and the
    /// ammo readout is on). <paramref name="s"/> is the vehicle's interpolated state, <paramref name="rt"/> the render tick.
    /// </summary>
    public bool RenderFlak(CarView v, in Interpolator.Sample s, double rt, float dt)
    {
        var f = v.Flak!;
        var def = _ctx.Vehicles!.Vehicles[v.Kind].Flak!;
        _turretAims.TryGetValue(v.Id, out var aim);
        var mine = aim != null && aim.Gunner == _ctx.MyId && _ctx.Driving && !_ctx.Dead && _ctx.Me != null;
        float yaw, pitch;
        if (mine)
        {
            _ctx.Me!.Pitch = FlakRules.ClampPitch(def, _ctx.Me.Pitch); // the mouse look is the aim, kept inside the barrels' elevation
            yaw = _ctx.Me.Yaw; pitch = _ctx.Me.Pitch;
        }
        else if (aim?.Buf.At(rt) is { } a) { yaw = a.Yaw; pitch = a.Pitch; }
        else { yaw = s.Yaw; pitch = FlakRules.ClampPitch(def, 0); }
        f.Turret.Rotation = new Vector3(0, FlakRules.Wrap(yaw - s.Yaw), 0);
        f.Cradle.Rotation = new Vector3(pitch, 0, 0);
        f.Step(dt);
        if (!mine) return false;
        var body = v.Node.GlobalPosition;
        _ctx.Me!.GlobalPosition = body;
        // over the guns: fixed to the cradle behind the breeches and above the barrels, so the barrels run parallel to the view and the crosshair sits just above them
        _ctx.Me.Turret(f.Cradle.ToGlobal(GunnerEye), yaw, pitch);
        CameraOn = true;
        return true;
    }

    /// <summary>The gunner's ammo as the HUD readout: the rounds and the reserve, or a word while it reloads (never colour alone).</summary>
    public string FlakReadout() =>
        _flakStatus is not { } s ? Strings.F("hud.flak.ammo", "-", "-")
        : s.ReloadRemaining > 0 ? Strings.T("hud.flak.reload")
        : Strings.F("hud.flak.ammo", s.Magazine, s.Reserve);



    /// <summary>How fast a vehicle moves, forward (m/s): what a casing thrown from it keeps.</summary>
    public static Vector3 CarVelocity(CarView car) => -car.Node.GlobalTransform.Basis.Z * (car.Kmh / 3.6f);

    /// <summary>The seat a vehicle's gunner sits in, or -1.</summary>
    static int GunnerSeat(VehicleDef def) => def.Seats.FindIndex(s => s.Role == "gunner");

    /// <summary>The vehicle whose door gun <paramref name="player"/> is at, or null.</summary>
    public CarView? DoorGunCarOf(ushort player)
    {
        if (player == 0) return null;
        foreach (var (id, aim) in _turretAims)
            if (aim.Gunner == player && _ctx.Cars.TryGet(id, out var v) && v.DoorGun != null) return v;
        return null;
    }

    /// <summary>
    /// Poses one door gun for this frame. Returns true when the local player is its gunner (the camera is then at its sight and the ammo readout
    /// is on). <paramref name="rot"/> is the vehicle's attitude as drawn (pitch, yaw, roll), <paramref name="rt"/> the render tick.
    /// </summary>
    public bool RenderDoorGun(CarView v, Vector3 rot, double rt, float dt)
    {
        var nodes = v.DoorGun!;
        var vehicle = _ctx.Vehicles!.Vehicles[v.Kind];
        var def = vehicle.DoorGun!;
        var mine = _ctx.View.Mode.SeatKeysLive && _ctx.Me != null && _ctx.Cars.Seats is { } st && st.CarId == v.Id && st.Mine >= 0 && st.Mine == GunnerSeat(vehicle);
        if (mine)
        {
            // the look is the aim, and it is held inside the arc: the view cannot go where the gun cannot
            var aim = DoorGunRules.Aim(def, rot.Y, rot.X, rot.Z, _ctx.Me!.Yaw, _ctx.Me.Pitch);
            _ctx.Me.Yaw = aim.ViewYaw; _ctx.Me.Pitch = aim.ViewPitch;
            nodes.Pose(aim.Yaw, aim.Pitch, dt);
            _ctx.Me.GlobalPosition = v.Node.GlobalPosition;
            var p = v.Node.GlobalPosition;
            var eye = nodes.Sight?.GlobalPosition ?? DoorGunRules.Eye(def, DoorGunRules.GunPivot(def, new NVec3(p.X, p.Y, p.Z), rot.Y, rot.X, rot.Z, aim), rot.Y, rot.X, rot.Z, aim).ToGodot();
            _ctx.Me.Turret(eye, aim.ViewYaw, aim.ViewPitch);
            _rider.HideSelf(); // the pilot's body view, if this player has just come from that seat
            CameraOn = true;
            return true;
        }
        _turretAims.TryGetValue(v.Id, out var seen);
        if (seen?.Buf.At(rt) is { } a) nodes.Pose(a.Yaw, a.Pitch, dt);
        else nodes.Pose(0, 0, dt);
        return false;
    }

    /// <summary>
    /// A round of a door gun (the server's Shot with the vehicle gun's weapon byte, from a player at one): the tracer leaves the muzzle as it is drawn
    /// on this client, the barrel kicks, the muzzle flashes. False for any other shot, which is drawn the usual way.
    /// </summary>
    public bool OnDoorGunShot(ShotMsg sh)
    {
        var car = sh.Weapon == SpecialWeapons.VehicleGun ? DoorGunCarOf(sh.Shooter) : null;
        if (car?.DoorGun is not { } nodes) return false;
        _ctx.Logs?.Shots.Add((_ctx.Now, sh));
        _ctx.Sound.Shot(sh, false);
        var origin = (sh.Origin).ToGodot(); var end = (sh.End).ToGodot();
        ShotFx.Spawn(_ctx.Owned, nodes.Muzzle?.GlobalPosition ?? origin, end, false);
        _hulls.ShotHole(origin, end);
        _water.BulletSurfaceHit(origin, end);
        nodes.Kick();
        CasingFx.Eject(_ctx.Owned, nodes.Muzzle?.GlobalPosition ?? origin, end - origin, CasingKind.Heavy, CarVelocity(car));
        return true;
    }

    string _doorGunPrompt = "";
    double _doorGunPromptAt = -10;

    /// <summary>The door gunner's prompt line: the keys as they are bound. Rebuilt once a second in case one was rebound.</summary>
    public string DoorGunPrompt()
    {
        if (_ctx.Now - _doorGunPromptAt < 1.0) return _doorGunPrompt;
        _doorGunPromptAt = _ctx.Now;
        return _doorGunPrompt = Strings.F("hud.doorgun.prompt", InputBindings.Name("fire"), InputBindings.Name("reload"), InputBindings.Name("interact"));
    }

    int _doorGunMag = -1, _doorGunReserve = -1;
    bool _doorGunReloading;
    string _doorGunText = "";

    /// <summary>The door gunner's ammo as the HUD readout: rounds and reserve, or a word while it reloads (never colour alone). Rebuilt only when a number changes.</summary>
    public string DoorGunReadout()
    {
        if (_flakStatus is not { } s) return Strings.F("hud.doorgun.ammo", "-", "-");
        var reloading = s.ReloadRemaining > 0;
        if (s.Magazine == _doorGunMag && s.Reserve == _doorGunReserve && reloading == _doorGunReloading) return _doorGunText;
        _doorGunMag = s.Magazine; _doorGunReserve = s.Reserve; _doorGunReloading = reloading;
        return _doorGunText = reloading ? Strings.T("hud.doorgun.reload") : Strings.F("hud.doorgun.ammo", s.Magazine, s.Reserve);
    }
}

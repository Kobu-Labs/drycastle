using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// The utility helicopter's door gun on the client (<see cref="DoorGunRules"/>). Every client poses the model's mount (yaw), gun (pitch) and barrel
/// (recoil) from the turret record of the snapshot, which for a door gun carries the mount's yaw and the gun's pitch against the airframe. The
/// gunner's own client uses its own look at once, held inside the arc (the look cannot leave it either), and puts the camera at the model's
/// gunner sight, looking along the barrel. A round's tracer leaves the muzzle as it is drawn, the barrel kicks and the muzzle flashes, for
/// everyone. The gunner's ammo is the HUD readout, sent as the flak's is (<see cref="EventKind.FlakStatus"/>).
/// </summary>
public partial class NetClient
{
    /// <summary>The nodes of a door gun that move, found by name (the model's own, see assets/suchohrad-generated-models), and the barrel's recoil.</summary>
    sealed class DoorGunNodes
    {
        public Node3D Mount = null!, Gun = null!;
        public Node3D? Barrel, Muzzle, Sight;
        Vector3 _barrelRest;
        float _restYaw, _recoil, _kick;
        MuzzleFlash? _flash;

        /// <summary>How fast the barrel comes home after a round: the whole travel in this many seconds.</summary>
        const float ReturnSeconds = 0.07f;

        public static DoorGunNodes? Find(Node3D root, VehicleDoorGunDef def)
        {
            if (root.FindChild("gun_mount", true, false) is not Node3D mount || mount.FindChild("gun", true, false) is not Node3D gun) return null;
            var n = new DoorGunNodes
            {
                Mount = mount, Gun = gun, _restYaw = def.OutwardYawDeg * FlightMath.Deg2Rad, _recoil = def.RecoilM,
                Barrel = gun.FindChild("barrel", true, false) as Node3D, Muzzle = gun.FindChild("muzzle", true, false) as Node3D,
                Sight = gun.FindChild("gunner_sight", true, false) as Node3D,
            };
            if (n.Barrel != null) n._barrelRest = n.Barrel.Position;
            if (n.Muzzle != null) { n._flash = new MuzzleFlash(); n.Muzzle.AddChild(n._flash); }
            return n;
        }

        /// <summary>A round left: the barrel kicks back and the muzzle flashes.</summary>
        public void Kick()
        {
            _kick = 1f;
            _flash?.Flash(1.3f);
        }

        /// <summary>The mount's yaw from its rest direction and the gun's pitch, as the rules give them; the barrel's recoil runs out.</summary>
        public void Pose(float yaw, float pitch, float dt)
        {
            Mount.Rotation = new Vector3(0, _restYaw + yaw, 0);
            Gun.Rotation = new Vector3(pitch, 0, 0);
            _kick = Mathf.MoveToward(_kick, 0, dt / ReturnSeconds);
            if (Barrel != null) Barrel.Position = _barrelRest + new Vector3(0, 0, _kick * _recoil);
        }
    }

    /// <summary>The seat a vehicle's gunner sits in, or -1.</summary>
    static int GunnerSeat(VehicleDef def) => def.Seats.FindIndex(s => s.Role == "gunner");

    /// <summary>The vehicle whose door gun <paramref name="player"/> is at, or null.</summary>
    CarView? DoorGunCarOf(ushort player)
    {
        if (player == 0) return null;
        foreach (var (id, aim) in _turretAims)
            if (aim.Gunner == player && _cars.TryGetValue(id, out var v) && v.DoorGun != null) return v;
        return null;
    }

    /// <summary>
    /// Poses one door gun for this frame. Returns true when the local player is its gunner (the camera is then at its sight and the ammo readout
    /// is on). <paramref name="rot"/> is the vehicle's attitude as drawn (pitch, yaw, roll), <paramref name="rt"/> the render tick.
    /// </summary>
    bool RenderDoorGun(CarView v, Vector3 rot, double rt, float dt)
    {
        var nodes = v.DoorGun!;
        var vehicle = Vehicles!.Vehicles[v.Kind];
        var def = vehicle.DoorGun!;
        var mine = _driving && !Dead && _me != null && _gunship == null && !_controlling && _seats is { } st && st.CarId == v.Id && st.Mine >= 0 && st.Mine == GunnerSeat(vehicle);
        if (mine)
        {
            // the look is the aim, and it is held inside the arc: the view cannot go where the gun cannot
            var aim = DoorGunRules.Aim(def, rot.Y, rot.X, rot.Z, _me!.Yaw, _me.Pitch);
            _me.Yaw = aim.ViewYaw; _me.Pitch = aim.ViewPitch;
            nodes.Pose(aim.Yaw, aim.Pitch, dt);
            _me.GlobalPosition = v.Node.GlobalPosition;
            var p = v.Node.GlobalPosition;
            var eye = nodes.Sight?.GlobalPosition ?? G(DoorGunRules.Eye(def, DoorGunRules.Pivot(def, new NVec3(p.X, p.Y, p.Z), rot.Y, rot.X, rot.Z), rot.Y, rot.X, rot.Z, aim));
            _me.Turret(eye, aim.ViewYaw, aim.ViewPitch);
            HideSelfInCar(); // the pilot's body view, if this player has just come from that seat
            _flakCamera = true;
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
    bool OnDoorGunShot(ShotMsg sh)
    {
        if (sh.Weapon != SpecialWeapons.VehicleGun || DoorGunCarOf(sh.Shooter) is not { DoorGun: { } nodes }) return false;
        if (KeepEventLogs) _shotLog.Add((Now, sh));
        SfxShot(sh, false);
        var origin = G(sh.Origin); var end = G(sh.End);
        ShotFx.Spawn(Owned, nodes.Muzzle?.GlobalPosition ?? origin, end, false);
        ShotHole(origin, end);
        BulletSurfaceHit(origin, end);
        nodes.Kick();
        return true;
    }

    string _doorGunPrompt = "";
    double _doorGunPromptAt = -10;

    /// <summary>The door gunner's prompt line: the keys as they are bound. Rebuilt once a second in case one was rebound.</summary>
    string DoorGunPrompt()
    {
        if (Now - _doorGunPromptAt < 1.0) return _doorGunPrompt;
        _doorGunPromptAt = Now;
        return _doorGunPrompt = Strings.F("hud.doorgun.prompt", InputBindings.Name("fire"), InputBindings.Name("reload"), InputBindings.Name("interact"));
    }

    int _doorGunMag = -1, _doorGunReserve = -1;
    bool _doorGunReloading;
    string _doorGunText = "";

    /// <summary>The door gunner's ammo as the HUD readout: rounds and reserve, or a word while it reloads (never colour alone). Rebuilt only when a number changes.</summary>
    string DoorGunReadout()
    {
        if (_flakStatus is not { } s) return Strings.F("hud.doorgun.ammo", "-", "-");
        var reloading = s.ReloadRemaining > 0;
        if (s.Magazine == _doorGunMag && s.Reserve == _doorGunReserve && reloading == _doorGunReloading) return _doorGunText;
        _doorGunMag = s.Magazine; _doorGunReserve = s.Reserve; _doorGunReloading = reloading;
        return _doorGunText = reloading ? Strings.T("hud.doorgun.reload") : Strings.F("hud.doorgun.ammo", s.Magazine, s.Reserve);
    }
}

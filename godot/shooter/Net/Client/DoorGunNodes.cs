using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Casings;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>The nodes of a door gun that move, found by name (the model's own, see assets/drycastle-generated-models), and the barrel's recoil.</summary>
internal sealed class DoorGunNodes
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

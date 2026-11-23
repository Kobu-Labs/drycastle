using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Vehicles;

/// <summary>A vehicle-mounted gun (vehicles.json "gun"), fired from a gunner seat. Hitscan with its own magazine and reserve.</summary>
public class VehicleGunDef
{
    /// <summary>Damage of one round to a player (vehicles take it times bullet_damage_multiplier, as a rifle's does).</summary>
    public float Damage { get; set; } = 14;
    /// <summary>Seconds between rounds while the trigger is held.</summary>
    public float FireInterval { get; set; } = 0.1f;
    public float Range { get; set; } = 220;
    public float SpreadDegrees { get; set; } = 1.2f;
    public int Magazine { get; set; } = 60;
    /// <summary>Rounds carried outside the magazine; a reload moves them in.</summary>
    public int Reserve { get; set; } = 240;
    public float ReloadTime { get; set; } = 2.5f;
}

/// <summary>
/// A pintle mount for the vehicle's <see cref="VehicleGunDef"/> (vehicles.json "door_gun"): where it pivots, which way it points at rest and
/// the arc it swings in, all in the vehicle's own frame, so the arc tilts with the airframe. The arc is what keeps the rounds off the
/// vehicle's own hull, tail and rotor (<see cref="DoorGunRules"/>).
/// </summary>
public class VehicleDoorGunDef
{
    /// <summary>The gun's pivot in the vehicle's frame, [x, y, z] (x right, y up, z back): the mount's yaw axis and the gun's pitch axis meet here.</summary>
    public float[] Pivot { get; set; } = { 1.3f, 1.5f, 0.15f };
    /// <summary>Which way the gun points at rest, as a yaw in the vehicle's frame, degrees (0 is the nose, -90 out of the right side, 90 out of the left).</summary>
    public float OutwardYawDeg { get; set; } = -90;
    /// <summary>How far it swings either side of that, degrees.</summary>
    public float YawArcDeg { get; set; } = 70;
    /// <summary>Lowest and highest elevation, degrees. The maximum keeps the rounds under the rotor.</summary>
    public float MinPitchDeg { get; set; } = -55;
    public float MaxPitchDeg { get; set; } = 12;
    /// <summary>Metres from the pivot along the barrel to the muzzle, where the rounds and the tracers leave.</summary>
    public float MuzzleLength { get; set; } = 0.88f;
    /// <summary>The gunner's eye in the gun's own frame, [x, y, z] (x right of the gun, y up, z back along it): behind and above the receiver.</summary>
    public float[] Eye { get; set; } = { 0, 0.22f, 0.62f };
    /// <summary>Visual only: how far the barrel slides back on a round, metres.</summary>
    public float RecoilM { get; set; } = 0.05f;
    /// <summary>
    /// A ring mount (the amphibious transport's ring gun): the gun's pitch axis rides this far out from the yaw axis at <see cref="Pivot"/>, metres,
    /// on the side the gun points, so the gun swings round a gunner who stands at the centre. 0: a pintle, the two axes meet (the door gun).
    /// </summary>
    public float RingRadius { get; set; }
    /// <summary>
    /// A guard for what the gun must not be depressed into (the riders in front of a ring gun): within guard_arc_deg either side of
    /// <see cref="GuardYawDeg"/> (a yaw in the vehicle's frame, 0 is the nose) the gun goes no lower than guard_min_pitch_deg, and over the next
    /// guard_blend_deg it comes down to <see cref="MinPitchDeg"/>. guard_arc_deg 0: no guard.
    /// </summary>
    public float GuardYawDeg { get; set; }
    public float GuardArcDeg { get; set; }
    public float GuardBlendDeg { get; set; } = 20;
    public float GuardMinPitchDeg { get; set; }
}

/// <summary>
/// An anti-aircraft turret on a vehicle (vehicles.json "flak"), worked from the gunner seat: the gunner's view is the turret's aim, and each
/// round is a fast airburst shell (<see cref="FlakRules"/>). Magazine, reserve and reload work as a <see cref="VehicleGun"/>'s (<see cref="AsGun"/>).
/// </summary>
public class VehicleFlakDef
{
    /// <summary>The weapons.json weapon whose shot sound and tracer index the flak's rounds use.</summary>
    public string WeaponId { get; set; } = "m249";
    /// <summary>The gun cradle's pivot in the vehicle's frame, [x, y, z] (x right, y up, z back); the barrels swing about it.</summary>
    public float[] Pivot { get; set; } = { 0, 2.55f, 1.75f };
    /// <summary>Metres from the pivot along the barrels to the muzzle tips.</summary>
    public float MuzzleLength { get; set; } = 3.65f;
    /// <summary>Each of the two barrels sits this far to the side of the pivot; the rounds leave them in turn.</summary>
    public float BarrelSpacing { get; set; } = 0.27f;
    /// <summary>Seconds between rounds while the trigger is held.</summary>
    public float FireInterval { get; set; } = 0.25f;
    /// <summary>Shell speed, m/s (a straight line: no drop).</summary>
    public float ShellSpeed { get; set; } = 700;
    /// <summary>The shell bursts on its own this far out if it met nothing.</summary>
    public float MaxRange { get; set; } = 2500;
    public float SpreadDegrees { get; set; } = 0.25f;
    /// <summary>The fuse: the shell bursts at its closest approach once it passes within this many metres of an aircraft.</summary>
    public float ProximityRadius { get; set; } = 12;
    /// <summary>An airburst's reach: an aircraft inside it takes <see cref="Damage"/> out to <see cref="BurstFullRadius"/>, falling to <see cref="EdgeDamage"/> here.</summary>
    public float BurstRadius { get; set; } = 15;
    public float BurstFullRadius { get; set; } = 4;
    public float Damage { get; set; } = 150;
    public float EdgeDamage { get; set; } = 40;
    /// <summary>A shell that hits the ground, a body or a vehicle: the direct damage and the small blast (weak on purpose, the flak is for aircraft).</summary>
    public float GroundDamage { get; set; } = 30;
    public SplashDef GroundSplash { get; set; } = new() { Radius = 4, FullDamageRadius = 1.5f, EdgeDamage = 10, SelfDamageMultiplier = 0.5f, KnockbackSpeed = 3 };
    public int Magazine { get; set; } = 40;
    public int Reserve { get; set; } = 400;
    public float ReloadTime { get; set; } = 4;
    /// <summary>Lowest and highest barrel elevation, degrees. The minimum keeps the barrels clear of the cab.</summary>
    public float MinElevationDeg { get; set; } = 5;
    public float MaxElevationDeg { get; set; } = 85;
    /// <summary>Visual only: the tracer's length, and the radius of the puff an airburst shows (the damage reach is <see cref="BurstRadius"/>).</summary>
    public float TracerLength { get; set; } = 14;
    public float PuffRadius { get; set; } = 6;

    /// <summary>The magazine, reserve and reload as a vehicle gun's, so the same <see cref="VehicleGun"/> keeps the count.</summary>
    public VehicleGunDef AsGun() => new() { Damage = GroundDamage, FireInterval = FireInterval, Range = MaxRange, SpreadDegrees = SpreadDegrees, Magazine = Magazine, Reserve = Reserve, ReloadTime = ReloadTime };
}

namespace Drycastle.ShooterCore;

/// <summary>vehicles.json. Rules from the vehicles ticket (#77); driving numbers are placeholders tuned by driving.</summary>
public class VehiclesFile : IVersioned
{
    public int Version { get; set; }
    public float RunOverMinSpeed { get; set; } = 8;
    public float RunOverDamagePerSpeed { get; set; } = 10;
    public float RunOverMaxDamage { get; set; } = 100;
    public float AbandonReturnTime { get; set; } = 30;
    /// <summary>Remaining-HP fractions at which smoke stage 1, 2 and 3 start (descending); at 0 HP the vehicle explodes.</summary>
    public List<float> DamageStages { get; set; } = new() { 0.66f, 0.33f, 0.12f };
    /// <summary>Seconds a destroyed vehicle stays gone before it returns to its origin with full HP.</summary>
    public float DestroyedReturnTime { get; set; } = 30;
    /// <summary>Wall impact: speed lost in one tick above the minimum (m/s) hurts the vehicle by this much per m/s.</summary>
    public float CrashMinSpeedLoss { get; set; } = 6;
    public float CrashDamagePerSpeed { get; set; } = 8;
    /// <summary>Vehicle against vehicle (#253): how much of the closing speed comes back as a bounce (0 = the two stick, 1 = a perfect bounce). The speed each vehicle loses in the bump goes through the crash numbers above.</summary>
    public float BumpRestitution { get; set; } = 0.3f;
    /// <summary>Weapon damage against vehicles is the weapon's damage times these (hitscan bullets, rockets and their splash).</summary>
    public float BulletDamageMultiplier { get; set; } = 0.5f;
    public float RocketDamageMultiplier { get; set; } = 1.5f;
    /// <summary>Mass the run-over numbers above are tuned for (the car). A type with <see cref="VehicleDef.RunOverMass"/> scales its run-over damage by its mass over this.</summary>
    public float RunOverReferenceMass { get; set; } = 1200;
    /// <summary>How far from a vehicle's footprint (metres, measured to its nearest edge, not its centre) an on-foot player can still get in. A type's <see cref="VehicleDef.EnterRadius"/> overrides it.</summary>
    public float EnterRadius { get; set; } = 1f;
    public VehicleExplosionDef Explosion { get; set; } = new();
    public List<VehicleDef> Vehicles { get; set; } = new();

    public static VehiclesFile Load(string path)
    {
        return Validate(DataFile.Load<VehiclesFile>(path));
    }

    public static VehiclesFile Validate(VehiclesFile f)
    {
        if (f.Vehicles.Count == 0) throw new DataFileException("vehicles.json: no vehicles");
        if (f.Vehicles.Count > 16) throw new DataFileException("vehicles.json: at most 16 vehicle types (the type travels in 4 bits)");
        foreach (var v in f.Vehicles)
        {
            if (v.Hp <= 0) throw new DataFileException($"vehicles.json: {v.Id}: hp must be positive");
            if (v.TopSpeed <= 0 || v.Acceleration <= 0 || v.Brake <= 0 || v.TurnRadius <= 0 || v.MaxLateralAccel <= 0)
                throw new DataFileException($"vehicles.json: {v.Id}: top_speed, acceleration, brake, turn_radius and max_lateral_accel must be positive");
            if (v.HighSpeedRadiusFactor < 1) throw new DataFileException($"vehicles.json: {v.Id}: high_speed_radius_factor must be at least 1");
            if (v.Hood.Length != 3) throw new DataFileException($"vehicles.json: {v.Id}: hood is [x, y, z]");
            if (v.Variants is < 1 or > 16) throw new DataFileException($"vehicles.json: {v.Id}: variants must be 1..16");
            if (v.Paints.Count > 0 && v.PaintMaterial == "") throw new DataFileException($"vehicles.json: {v.Id}: paints need paint_material");
            if (v.Lights is { } l)
            {
                if (l.Head.Count is < 1 or > 4 || l.Tail.Count > 4) throw new DataFileException($"vehicles.json: {v.Id}: lights need 1 to 4 head and 0 to 4 tail lamps");
                if (l.Head.Concat(l.Tail).Any(p => p is null || p.Length != 3)) throw new DataFileException($"vehicles.json: {v.Id}: each lamp is [x, y, z]");
                if (l.BeamRange <= 0 || l.BeamAngle <= 0 || l.BeamAngle >= 89) throw new DataFileException($"vehicles.json: {v.Id}: beam_range must be positive, beam_angle 1..88 degrees");
            }
            if (v.Glass.Count > 24) throw new DataFileException($"vehicles.json: {v.Id}: at most 24 glass panes");
            foreach (var pane in v.Glass)
            {
                if (pane is null || pane.Centre.Length != 3 || pane.Normal.Length != 3 || pane.Size.Length != 2) throw new DataFileException($"vehicles.json: {v.Id}: a glass pane needs centre [x, y, z], normal [x, y, z] and size [width, height]");
                if (pane.Normal.Sum(x => x * x) < 1e-6f || !(pane.Size[0] > 0) || !(pane.Size[1] > 0) || pane.Hp is <= 0) throw new DataFileException($"vehicles.json: {v.Id}: glass pane {pane.Id}: the normal must not be zero, size and hp must be positive");
                if (pane.OutlineProblem() is { } problem) throw new DataFileException($"vehicles.json: {v.Id}: glass pane {pane.Id}: {problem}");
            }
            if (v.Glass.Select(g => g.Id).Distinct().Count() != v.Glass.Count) throw new DataFileException($"vehicles.json: {v.Id}: glass pane ids must differ");
            if (v.OpenSides.Count > 8) throw new DataFileException($"vehicles.json: {v.Id}: at most 8 open sides");
            foreach (var side in v.OpenSides)
            {
                if (side is null || side.Centre.Length != 3 || side.Normal.Length != 3 || side.Size.Length != 2) throw new DataFileException($"vehicles.json: {v.Id}: an open side needs centre [x, y, z], normal [x, y, z] and size [width, height]");
                if (side.Normal.Sum(x => x * x) < 1e-6f || !(side.Size[0] > 0) || !(side.Size[1] > 0)) throw new DataFileException($"vehicles.json: {v.Id}: open side {side.Id}: the normal must not be zero and the size must be positive");
                if (side.OutlineProblem() is { } problem) throw new DataFileException($"vehicles.json: {v.Id}: open side {side.Id}: {problem}");
            }
            if (v.Tint is { Length: not 3 } || v.Tint != null && v.Paints.Count > 0) throw new DataFileException($"vehicles.json: {v.Id}: tint is [r, g, b] and excludes paints");
            if (v.Seats.Count is < 1 or > 4 || v.Seats[0].Role != "driver") throw new DataFileException($"vehicles.json: {v.Id}: seats are 1..4 and the first is the driver");
            foreach (var seat in v.Seats)
            {
                if (seat.Role is not ("driver" or "gunner" or "passenger")) throw new DataFileException($"vehicles.json: {v.Id}: seat role {seat.Role} is not driver, gunner or passenger");
                if (seat.Role == "driver" && v.Seats.Count(x => x.Role == "driver") != 1) throw new DataFileException($"vehicles.json: {v.Id}: one driver seat");
                if (seat.Offset.Length != 3) throw new DataFileException($"vehicles.json: {v.Id}: seat offset is [x, y, z]");
                if (seat.Armed && seat.Role != "passenger") throw new DataFileException($"vehicles.json: {v.Id}: only a passenger seat can be armed (a driver drives, a gunner has the vehicle's gun)");
                if (seat.Standing && !seat.Exposed) throw new DataFileException($"vehicles.json: {v.Id}: a standing seat is an exposed seat");
                if (seat.Aim is { } aim)
                {
                    if (!seat.Armed) throw new DataFileException($"vehicles.json: {v.Id}: a seat's aim is for an armed seat");
                    if (aim.ArcDeg is <= 0 or > 180 || aim.MinPitchDeg < -89 || aim.MaxPitchDeg > 89 || aim.MinPitchDeg >= aim.MaxPitchDeg || aim.SpreadFullSpeed <= 0)
                        throw new DataFileException($"vehicles.json: {v.Id}: a seat's aim needs arc_deg 0..180, min_pitch_deg below max_pitch_deg (within -89..89) and a positive spread_full_speed");
                }
            }
            if (v.ExposedGunner && !v.Seats.Any(x => x.Role == "gunner")) throw new DataFileException($"vehicles.json: {v.Id}: exposed_gunner needs a gunner seat");
            if (v.Gun is { } g)
            {
                if (g.Damage <= 0 || g.FireInterval <= 0 || g.Range <= 0 || g.Magazine < 1 || g.Reserve < 0 || g.ReloadTime < 0)
                    throw new DataFileException($"vehicles.json: {v.Id}: gun damage, fire_interval, range and magazine must be positive, reserve and reload_time not negative");
                if (!v.Seats.Any(x => x.Role == "gunner")) throw new DataFileException($"vehicles.json: {v.Id}: a gun needs a gunner seat");
            }
            if (v.Flak is { } fk)
            {
                if (v.Gun != null) throw new DataFileException($"vehicles.json: {v.Id}: a vehicle has a gun or a flak turret, not both");
                if (!v.Seats.Any(x => x.Role == "gunner")) throw new DataFileException($"vehicles.json: {v.Id}: a flak turret needs a gunner seat");
                if (v.Medium != VehicleMedium.Land) throw new DataFileException($"vehicles.json: {v.Id}: a flak turret is for a land vehicle");
                if (string.IsNullOrEmpty(fk.WeaponId) || fk.Pivot.Length != 3) throw new DataFileException($"vehicles.json: {v.Id}: flak needs weapon_id and pivot [x, y, z]");
                if (fk.FireInterval <= 0 || fk.ShellSpeed <= 0 || fk.MaxRange <= 0 || fk.MuzzleLength <= 0 || fk.BarrelSpacing < 0 || fk.SpreadDegrees < 0
                    || fk.ProximityRadius <= 0 || fk.BurstRadius <= 0 || fk.Damage <= 0 || fk.EdgeDamage < 0 || fk.GroundDamage <= 0 || fk.Magazine < 1 || fk.Reserve < 0 || fk.ReloadTime < 0
                    || fk.TracerLength <= 0 || fk.PuffRadius <= 0)
                    throw new DataFileException($"vehicles.json: {v.Id}: flak numbers must be positive (spread, edge damage, reserve, reload_time and barrel_spacing not negative)");
                if (fk.BurstFullRadius < 0 || fk.BurstFullRadius >= fk.BurstRadius) throw new DataFileException($"vehicles.json: {v.Id}: flak burst_full_radius must be 0 or more and less than burst_radius");
                if (fk.ProximityRadius > fk.BurstRadius) throw new DataFileException($"vehicles.json: {v.Id}: flak proximity_radius must not exceed burst_radius");
                if (fk.GroundSplash.Radius <= 0 || fk.GroundSplash.FullDamageRadius < 0 || fk.GroundSplash.FullDamageRadius >= fk.GroundSplash.Radius)
                    throw new DataFileException($"vehicles.json: {v.Id}: flak ground_splash needs radius > full_damage_radius >= 0");
                if (fk.MinElevationDeg < -10 || fk.MaxElevationDeg > 89 || fk.MinElevationDeg >= fk.MaxElevationDeg)
                    throw new DataFileException($"vehicles.json: {v.Id}: flak elevation is min_elevation_deg (at least -10) below max_elevation_deg (at most 89)");
            }
            if (v.ChaseDistance is <= 0 || v.ChaseHeight is < 0) throw new DataFileException($"vehicles.json: {v.Id}: chase_distance must be positive and chase_height not negative");
            if (v.Height <= 0 || v.HalfWidth <= 0 || v.HalfLength <= 0) throw new DataFileException($"vehicles.json: {v.Id}: height, half_width and half_length must be positive");
            if (v.EnterRadius is < 0 || f.EnterRadius < 0) throw new DataFileException($"vehicles.json: {v.Id}: enter_radius must not be negative");
            if (v.RunOverMass is { } rm && rm <= 0) throw new DataFileException($"vehicles.json: {v.Id}: run_over_mass must be positive");
            if (!VehicleMedium.All.Contains(v.Medium)) throw new DataFileException($"vehicles.json: {v.Id}: medium {v.Medium} is not land, amphibious, boat or air");
            if (v.CanFloat && (v.WaterTopSpeed <= 0 || v.WaterAcceleration <= 0 || v.LandDecel <= 0 || v.PlaningSpeed < 0))
                throw new DataFileException($"vehicles.json: {v.Id}: water_top_speed, water_acceleration and land_decel must be positive, planing_speed not negative");
            if (v.Medium == VehicleMedium.Boat && v.Gun != null) throw new DataFileException($"vehicles.json: {v.Id}: a boat carries no gun");
            if (v.DoorGun is { } dg)
            {
                if (v.Gun == null) throw new DataFileException($"vehicles.json: {v.Id}: a door_gun fires by the gun rules: it needs a gun block");
                if (v.Flak != null) throw new DataFileException($"vehicles.json: {v.Id}: a vehicle has a door gun or a flak turret, not both");
                if (dg.Pivot.Length != 3 || dg.Eye.Length != 3) throw new DataFileException($"vehicles.json: {v.Id}: door_gun pivot and eye are [x, y, z]");
                if (dg.YawArcDeg is <= 0 or > 180 || dg.MinPitchDeg < -89 || dg.MaxPitchDeg > 89 || dg.MinPitchDeg >= dg.MaxPitchDeg || dg.MuzzleLength <= 0 || dg.RecoilM < 0)
                    throw new DataFileException($"vehicles.json: {v.Id}: door_gun needs yaw_arc_deg 0..180, min_pitch_deg below max_pitch_deg (within -89..89), a positive muzzle_length and recoil_m not negative");
                if (dg.RingRadius < 0 || dg.GuardArcDeg is < 0 or > 180 || dg.GuardBlendDeg < 0 || dg.GuardArcDeg > 0 && (dg.GuardMinPitchDeg < dg.MinPitchDeg || dg.GuardMinPitchDeg >= dg.MaxPitchDeg))
                    throw new DataFileException($"vehicles.json: {v.Id}: door_gun ring_radius and guard_blend_deg not negative, guard_arc_deg 0..180, guard_min_pitch_deg from min_pitch_deg up to below max_pitch_deg");
            }
            if (v.Drive is { } dr)
            {
                if (v.Medium != VehicleMedium.Amphibious) throw new DataFileException($"vehicles.json: {v.Id}: a drive block is for an amphibious vehicle");
                if (dr.Validate(v) is { } why) throw new DataFileException($"vehicles.json: {v.Id}: drive: {why}");
            }
            if ((v.Medium == VehicleMedium.Air) != (v.Flight != null)) throw new DataFileException($"vehicles.json: {v.Id}: medium air and a flight block go together");
            if (v.Flight is { } fl)
            {
                if (v.Flak != null) throw new DataFileException($"vehicles.json: {v.Id}: a flak turret is for a land vehicle");
                if (fl.Validate() is { } why) throw new DataFileException($"vehicles.json: {v.Id}: flight: {why}");
                if (v.Acceleration / v.TopSpeed < fl.DragLow) throw new DataFileException($"vehicles.json: {v.Id}: flight: drag_low must not exceed acceleration / top_speed (the drag alone would hold it below its top speed)");
            }
        }
        if (f.DamageStages.Count != 3 || f.DamageStages.Any(x => x <= 0 || x >= 1) || f.DamageStages[0] <= f.DamageStages[1] || f.DamageStages[1] <= f.DamageStages[2])
            throw new DataFileException("vehicles.json: damage_stages are three descending fractions between 0 and 1");
        if (f.Explosion.Splash.Radius <= 0) throw new DataFileException("vehicles.json: explosion.splash.radius must be positive");
        if (f.BumpRestitution is < 0 or > 1) throw new DataFileException("vehicles.json: bump_restitution is between 0 and 1");
        if (f.RunOverReferenceMass <= 0) throw new DataFileException("vehicles.json: run_over_reference_mass must be positive");
        return f;
    }

    /// <summary>Run-over damage multiplier of a type: its run_over_mass over the reference mass, or 1 when it has none (the v1 numbers).</summary>
    public float EnterRadiusOf(VehicleDef v) => v.EnterRadius ?? EnterRadius;

    public float RunOverScale(VehicleDef v) => v.RunOverMass is { } m ? m / RunOverReferenceMass : 1f;
}

/// <summary>vehicles.json "medium" values (see <see cref="VehicleDef.Medium"/>).</summary>
public static class VehicleMedium
{
    public const string Land = "land", Amphibious = "amphibious", Boat = "boat", Air = "air";
    public static readonly string[] All = { Land, Amphibious, Boat, Air };
}

/// <summary>One seat of a vehicle (vehicles.json "seats"). Seat 0 is always the driver. Offset is in the vehicle's frame, metres: x right, y up, z back (forward is -Z), origin on the ground at the car's centre.</summary>
public class VehicleSeatDef
{
    /// <summary>"driver" (seat 0 only, drives), "gunner" (fires the vehicle's gun) or "passenger".</summary>
    public string Role { get; set; } = "passenger";
    public float[] Offset { get; set; } = { 0, 0, 0 };
    /// <summary>
    /// An armed seat (ADR 0011): its rider keeps their own loadout and fights with it from the seat, in first person, aiming with the view inside
    /// <see cref="Aim"/>. A passenger seat only; false keeps the rider's hands empty, as on the bus.
    /// </summary>
    public bool Armed { get; set; }
    /// <summary>
    /// The hull does not cover this seat's rider (ADR 0012): a shot that reaches their body hits them and not the hull (<see cref="GunnerCover"/>).
    /// False keeps the rider behind the hull, as in every closed vehicle. (The flak truck and the utility helicopter say it for their gunner with the
    /// vehicle's <see cref="VehicleDef.ExposedGunner"/>.)
    /// </summary>
    public bool Exposed { get; set; }
    /// <summary>The rider of an exposed seat stands (the gunner in a ring) instead of sitting: that is how they are drawn, and how tall they are to a bullet.</summary>
    public bool Standing { get; set; }
    /// <summary>How far round and up and down an armed seat's rider can aim. Null: all the way round.</summary>
    public VehicleSeatAimDef? Aim { get; set; }

    /// <summary>The stance an exposed rider has to a bullet and to the eye: seated riders have the crouch's height.</summary>
    public Stance RiderStance => Standing ? Stance.Stand : Stance.Crouch;
}

/// <summary>
/// The arc an armed seat's rider aims in (vehicles.json seat "aim"), in the vehicle's own frame so it turns and tilts with it: what the seat, the
/// rider beside it and the rest of the vehicle leave free. The view is held inside it, and so are the shots (<see cref="SeatAimRules"/>).
/// </summary>
public class VehicleSeatAimDef
{
    /// <summary>The middle of the arc as a yaw in the vehicle's frame, degrees (0 is the nose, -90 out of the right side, 90 out of the left).</summary>
    public float YawDeg { get; set; }
    /// <summary>How far the aim swings either side of that, degrees (180: all the way round).</summary>
    public float ArcDeg { get; set; } = 180;
    /// <summary>Lowest and highest aim, degrees.</summary>
    public float MinPitchDeg { get; set; } = -60;
    public float MaxPitchDeg { get; set; } = 80;
    /// <summary>
    /// The vehicle's speed (m/s) at which the rider's shots carry the weapon's whole movement spread, as their own sprint does on foot; below it in
    /// proportion, none at a standstill. Higher: steadier at speed.
    /// </summary>
    public float SpreadFullSpeed { get; set; } = 16;
}

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

/// <summary>
/// The handling of a vehicle that flies (vehicles.json "flight", with medium "air"): every number <see cref="FlightModel"/> reads beyond the type's
/// <see cref="VehicleDef.TopSpeed"/> (level flight, m/s) and <see cref="VehicleDef.Acceleration"/> (m/s² at full nose-down tilt). Angles are degrees,
/// rates per second. The summaries say what turning a number up does.
/// </summary>
public class VehicleFlightDef
{
    // ---- cyclic: tilt and what it buys
    /// <summary>Sideways acceleration at full bank, m/s². Higher: faster strafing and a higher sideways top speed.</summary>
    public float StrafeAcceleration { get; set; } = 9;
    /// <summary>Nose-down tilt at full forward input. Visual only above a few degrees (the acceleration is the type's own number): higher looks more aggressive.</summary>
    public float MaxPitchDeg { get; set; } = 22;
    /// <summary>Nose-up tilt at full backward input. Backward acceleration is the forward one times sin(this) / sin(max_pitch_deg), so higher flies backward faster.</summary>
    public float MaxPitchBackDeg { get; set; } = 14;
    /// <summary>Bank at full sideways input (visual, as max_pitch_deg).</summary>
    public float MaxRollDeg { get; set; } = 26;
    /// <summary>How fast the airframe reaches the tilt asked for, rad/s (a critically damped spring: about 4 / this seconds to settle). Higher: snappier and lighter; lower: heavier.</summary>
    public float AttitudeResponse { get; set; } = 5.5f;
    /// <summary>Keyboard ramp, per second: a held key reaches full deflection in 1 / input_rise seconds, a released one is gone in 1 / input_fall. Higher: taps bite harder.</summary>
    public float InputRise { get; set; } = 5;
    public float InputFall { get; set; } = 8;
    /// <summary>Air drag at low speed, per second. Higher: it bleeds speed faster on its own and feels stickier around the hover. The drag at speed is derived so that full tilt ends at top_speed.</summary>
    public float DragLow { get; set; } = 0.12f;
    /// <summary>How many times more drag it meets flying sideways or backwards than nose first. Higher: slower sideways and backward top speeds, and a turn costs more speed.</summary>
    public float OffAxisDrag { get; set; } = 3;

    // ---- hands off: the stabiliser
    /// <summary>Hands off, the airframe tilts against its own motion by this many degrees per m/s (the flare). Higher: stops shorter; too high and it rocks back (the tests hold the bound).</summary>
    public float BrakeGainDeg { get; set; } = 1.3f;
    /// <summary>The most the flare tilts, degrees. Higher: a harder stop from high speed.</summary>
    public float BrakeTiltDeg { get; set; } = 18;
    /// <summary>A small constant deceleration hands off, m/s², that ends the last creep so the hover is exactly still.</summary>
    public float HoverDecel { get; set; } = 0.6f;

    // ---- yaw and turns
    /// <summary>Fastest the nose turns after the view, degrees per second.</summary>
    public float MaxYawRateDeg { get; set; } = 110;
    /// <summary>How eagerly the nose follows the view, rad/s (critically damped, as attitude_response).</summary>
    public float YawResponse { get; set; } = 6;
    /// <summary>Bank into a turn, degrees per m/s² of turn acceleration (forward speed times yaw rate). Higher: it leans further for the same turn.</summary>
    public float TurnBankGain { get; set; } = 1.2f;
    /// <summary>The most a turn adds to the bank, degrees.</summary>
    public float TurnBankMaxDeg { get; set; } = 24;
    /// <summary>How fast the direction of travel swings round to the nose in forward flight, per second. Higher: it carves like on rails; 0: the nose turns and it keeps sliding the old way.</summary>
    public float TurnGrip { get; set; } = 2.4f;
    /// <summary>Forward speed (m/s) at which turn_grip is fully in; below it the helicopter is free to slide sideways and backwards.</summary>
    public float CoordinationSpeed { get; set; } = 12;

    // ---- collective: up and down
    /// <summary>Climb and descent rates with the key held, m/s.</summary>
    public float ClimbRate { get; set; } = 9;
    public float DescendRate { get; set; } = 8;
    /// <summary>How fast the vertical speed follows the key, per second, and the most it may change, m/s². Higher: the lift bites at once; lower: it floats.</summary>
    public float VerticalResponse { get; set; } = 3.5f;
    public float VerticalAccel { get; set; } = 9;
    /// <summary>Below this height over the ground the descent is slowed (the cushion), down to touchdown_speed at the ground, m and m/s.</summary>
    public float CushionHeight { get; set; } = 7;
    public float TouchdownSpeed { get; set; } = 1.5f;
    /// <summary>A flying vehicle with run_over runs people over only while it is no higher than this over the ground, m (skids and fuselage skimming them).</summary>
    public float RunOverAgl { get; set; } = 1.5f;
    /// <summary>Deceleration of the skids sliding on the ground, m/s².</summary>
    public float GroundFriction { get; set; } = 10;
    /// <summary>Highest it climbs above the ground under it, metres; the climb fades out over the last ceiling_fade metres.</summary>
    public float Ceiling { get; set; } = 160;
    public float CeilingFade { get; set; } = 25;
    /// <summary>Within this many metres of the map's edge the speed towards the edge is held down, to nothing at the edge itself.</summary>
    public float EdgeMargin { get; set; } = 60;

    // ---- rotor and the empty pilot seat
    /// <summary>Seconds the rotor takes to full speed once a pilot sits down, and to stop once it stands on the ground without one.</summary>
    public float SpoolUpTime { get; set; } = 3;
    public float SpoolDownTime { get; set; } = 6;
    /// <summary>Rotor speed (0 to 1) it needs before it lifts off.</summary>
    public float LiftoffRotor { get; set; } = 0.85f;
    /// <summary>With riders but no pilot it holds a hover this long, then sinks at autorotate_descent m/s to the ground. With nobody aboard it sinks at once.</summary>
    public float PilotlessHoverTime { get; set; } = 4;
    public float AutorotateDescent { get; set; } = 3;
    /// <summary>Hit points lost per second while the skids are in a water body.</summary>
    public float WaterDamagePerSecond { get; set; } = 60;

    // ---- what collides
    /// <summary>The rotor as the world sees it: a disc of this radius (smaller than the blades, on purpose), this high above the skids and this thick, metres.</summary>
    public float RotorRadius { get; set; } = 4.5f;
    public float RotorHeight { get; set; } = 3.4f;
    public float RotorThickness { get; set; } = 0.3f;

    /// <summary>The pilot's chase camera.</summary>
    public FlightCameraDef Camera { get; set; } = new();

    /// <summary>Null when the numbers make a flyable helicopter, else what is wrong.</summary>
    public string? Validate()
    {
        if (StrafeAcceleration <= 0 || AttitudeResponse <= 0 || InputRise <= 0 || InputFall <= 0 || YawResponse <= 0 || MaxYawRateDeg <= 0 || ClimbRate <= 0 || DescendRate <= 0
            || VerticalResponse <= 0 || VerticalAccel <= 0 || CushionHeight <= 0 || TouchdownSpeed <= 0 || GroundFriction <= 0 || Ceiling <= 0 || CeilingFade <= 0
            || SpoolUpTime <= 0 || SpoolDownTime <= 0 || AutorotateDescent <= 0 || RotorRadius <= 0 || RotorHeight <= 0 || RotorThickness <= 0 || CoordinationSpeed <= 0)
            return "rates, times and sizes must be positive";
        if (MaxPitchDeg is <= 0 or > 60 || MaxPitchBackDeg is <= 0 or > 60 || MaxRollDeg is <= 0 or > 60 || BrakeTiltDeg is < 0 or > 60 || TurnBankMaxDeg is < 0 or > 60) return "tilt limits are 0..60 degrees";
        if (OffAxisDrag < 1) return "off_axis_drag is at least 1";
        if (DragLow < 0 || BrakeGainDeg < 0 || HoverDecel < 0 || TurnBankGain < 0 || TurnGrip < 0 || EdgeMargin < 0 || PilotlessHoverTime < 0 || WaterDamagePerSecond < 0) return "gains must not be negative";
        if (LiftoffRotor is <= 0 or > 1) return "liftoff_rotor is above 0 and at most 1";
        // the attitude and yaw springs are stepped once a tick: past this they would ring instead of settling
        if (AttitudeResponse >= 0.5f * Wire.TickRate || YawResponse >= 0.5f * Wire.TickRate) return $"attitude_response and yaw_response must stay below {0.5f * Wire.TickRate} at {Wire.TickRate} ticks a second";
        return Camera.Validate();
    }
}

/// <summary>The pilot's chase camera (vehicles.json "flight.camera"). The view turns with the mouse at once; the camera's place follows the helicopter on a spring.</summary>
public class FlightCameraDef
{
    /// <summary>Metres behind and above the helicopter.</summary>
    public float Distance { get; set; } = 13;
    public float Height { get; set; } = 3.6f;
    /// <summary>How tightly the camera follows, rad/s (critically damped). Lower: more lag, the helicopter pulls away as it accelerates.</summary>
    public float Follow { get; set; } = 9;
    /// <summary>The furthest the helicopter may pull away from its place in the frame, metres.</summary>
    public float MaxLag { get; set; } = 3.5f;
    /// <summary>How much of the airframe's pitch and roll the camera takes (0 to 1): the horizon tilts by this share.</summary>
    public float Tilt { get; set; } = 0.4f;
    /// <summary>Degrees the field of view widens by at top speed.</summary>
    public float FovBoostDeg { get; set; } = 9;

    public string? Validate() =>
        Distance <= 0 || Follow <= 0 || MaxLag < 0 || Tilt is < 0 or > 1 || FovBoostDeg is < 0 or > 40 ? "camera: distance and follow positive, tilt 0..1, fov_boost_deg 0..40" : null;
}

/// <summary>
/// One glass pane of a vehicle type (vehicles.json "glass", ADR 0007, superseded in part by ADR 0008): a flat pane in the vehicle frame
/// (forward -Z, origin on the ground, as the hood), the server tests shots against it and the client draws the crack on it.
/// <see cref="Size"/> is [width, height] of its bounding rectangle: the width runs horizontally, the height up the pane (its slant length).
/// <see cref="Outline"/> is its real shape inside that rectangle. A generated model (bus, flak truck, helicopter) writes the list with its
/// script and has the pane as a node <c>glass_N</c>, N being the pane's place in the list.
/// </summary>
public class VehicleGlassDef
{
    public string Id { get; set; } = "";
    public float[] Centre { get; set; } = { 0, 0, 0 };
    /// <summary>The outward direction of the pane (any length).</summary>
    public float[] Normal { get; set; } = { 0, 0, -1 };
    public float[] Size { get; set; } = { 1, 1 };
    /// <summary>
    /// The pane's shape: a convex polygon of 3 to 12 points [[u, v], ...] in metres from <see cref="Centre"/>, u along the pane's width, v up it,
    /// counter-clockwise seen from outside, inside the <see cref="Size"/> box. Null: the whole rectangle.
    /// </summary>
    public float[][]? Outline { get; set; }
    /// <summary>Hit points of this pane; null takes glass.json vehicle_pane_hp.</summary>
    public float? Hp { get; set; }

    public GlassPaneRect Rect() => GlassPaneRect.Make(new System.Numerics.Vector3(Centre[0], Centre[1], Centre[2]), new System.Numerics.Vector3(Normal[0], Normal[1], Normal[2]), Size[0], Size[1],
        Outline?.Select(p => new System.Numerics.Vector2(p[0], p[1])).ToList());

    /// <summary>Null when the outline is fine (or there is none), else what is wrong with it.</summary>
    public string? OutlineProblem()
    {
        if (Outline == null) return null;
        if (Outline.Length is < 3 or > 12 || Outline.Any(p => p is null || p.Length != 2 || !float.IsFinite(p[0]) || !float.IsFinite(p[1]))) return "an outline is 3 to 12 points [u, v]";
        const float eps = 0.002f;
        if (Outline.Any(p => MathF.Abs(p[0]) > Size[0] / 2 + eps || MathF.Abs(p[1]) > Size[1] / 2 + eps)) return "the outline must lie inside the size box";
        float area = 0;
        for (var i = 0; i < Outline.Length; i++)
        {
            float[] a = Outline[i], b = Outline[(i + 1) % Outline.Length], c = Outline[(i + 2) % Outline.Length];
            area += a[0] * b[1] - b[0] * a[1];
            if ((b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0]) < -1e-4f) return "the outline must be convex and counter-clockwise";
        }
        return area > 1e-4f ? null : "the outline must be counter-clockwise and have an area";
    }
}

/// <summary>
/// The handling of a vehicle its driver's client predicts (vehicles.json "drive", ADR 0010: the amphibious transport): every number
/// <see cref="DriveModel"/> reads beyond the type's <see cref="VehicleDef.TopSpeed"/>, <see cref="VehicleDef.Acceleration"/>,
/// <see cref="VehicleDef.Brake"/>, <see cref="VehicleDef.TurnRadius"/>, <see cref="VehicleDef.HighSpeedRadiusFactor"/>,
/// <see cref="VehicleDef.WaterTopSpeed"/> and <see cref="VehicleDef.WaterAcceleration"/>. Rates are per second, angles degrees. The summaries say
/// what turning a number up does.
/// </summary>
public class VehicleDriveDef
{
    // ---- the keyboard ramp
    /// <summary>A held throttle key reaches full in 1 / throttle_rise seconds, a released one is gone in 1 / throttle_fall. Higher: taps bite harder.</summary>
    public float ThrottleRise { get; set; } = 4;
    public float ThrottleFall { get; set; } = 7;
    /// <summary>The same for the steering keys at a standstill: full lock in 1 / steer_rise seconds, centred again in 1 / steer_fall.</summary>
    public float SteerRise { get; set; } = 4.5f;
    public float SteerFall { get; set; } = 8;
    /// <summary>How much of steer_rise is left at top speed (0 to 1). Lower: the wheel winds on more slowly when fast, so the steering is calmer there.</summary>
    public float SteerCalm { get; set; } = 0.45f;

    // ---- where the wheels are (the model's)
    /// <summary>Distance between the axles and between the wheels of an axle, metres: where the ground is sampled, so the body lies on the slope under it.</summary>
    public float Wheelbase { get; set; } = 2.72f;
    public float Track { get; set; } = 1.96f;

    public DriveLandDef Land { get; set; } = new();
    public DriveWaterDef Water { get; set; } = new();
    public DriveBodyDef Body { get; set; } = new();
    public DriveCameraDef Camera { get; set; } = new();

    /// <summary>Null when the numbers make a drivable vehicle, else what is wrong.</summary>
    public string? Validate(VehicleDef v)
    {
        if (ThrottleRise <= 0 || ThrottleFall <= 0 || SteerRise <= 0 || SteerFall <= 0 || Wheelbase <= 0 || Track <= 0) return "the ramp rates, wheelbase and track must be positive";
        if (SteerCalm is <= 0 or > 1) return "steer_calm is above 0 and at most 1";
        var l = Land;
        if (l.LaunchBoost < 1 || l.LaunchSpeed <= 0 || l.PowerCurve <= 0 || l.CoastDecel < 0 || l.CoastDrag < 0 || l.Grip <= 0 || l.YawResponse <= 0 || l.HandbrakeDecel <= 0
            || l.HandbrakeSteer <= 0 || l.StepHeight <= 0 || l.SnapDown <= 0)
            return "land: launch_boost at least 1; launch_speed, power_curve, grip, yaw_response, handbrake_decel, handbrake_steer, step_height and snap_down positive; coast_decel and coast_drag not negative";
        if (l.BrakeBite is < 0 or > 1 || l.HandbrakeGrip is <= 0 or > 1 || l.ReverseTopFraction is <= 0 or > 1) return "land: brake_bite 0..1, handbrake_grip and reverse_top_fraction above 0 and at most 1";
        if (l.MaxClimbDeg is <= 0 or >= 80) return "land: max_climb_deg is 1..79";
        var w = Water;
        if (w.Draft < 0 || w.FloatDepth <= 0 || w.ReverseThrust is <= 0 or > 1 || w.CoastDrag < 0 || w.TurnRadius <= 0 || w.PropSteer < 0 || w.YawResponse <= 0 || w.SideDrag <= 0
            || w.SternSwing < 0 || w.BuoyancyResponse <= 0 || w.BuoyancyDamping <= 0 || w.ClimbOutRate <= 0)
            return "water: float_depth, turn_radius, yaw_response, side_drag, buoyancy_response, buoyancy_damping and climb_out_rate positive; reverse_thrust above 0 and at most 1; draft, coast_drag, prop_steer and stern_swing not negative";
        if (w.CoastDrag * v.WaterTopSpeed >= v.WaterAcceleration) return "water: coast_drag times water_top_speed must stay below water_acceleration (the drag alone would hold it below its top speed)";
        // the buoyancy spring is stepped once a tick: past this it would ring instead of settling
        if (w.BuoyancyResponse >= 0.5f * Wire.TickRate) return $"water: buoyancy_response must stay below {0.5f * Wire.TickRate} at {Wire.TickRate} ticks a second";
        return Body.Validate() ?? Camera.Validate();
    }
}

/// <summary>The drive on its wheels (vehicles.json "drive.land").</summary>
public class DriveLandDef
{
    /// <summary>Extra pull from a standstill: the acceleration is multiplied by this at 0 m/s, fading to 1 at launch_speed (m/s). Higher: a harder launch, the same top end.</summary>
    public float LaunchBoost { get; set; } = 1.7f;
    public float LaunchSpeed { get; set; } = 9;
    /// <summary>How the pull fades towards top speed: acceleration times 1 - (speed / top)^power_curve. Higher: it pulls hard for longer and tops out abruptly; 1: it eases off all the way.</summary>
    public float PowerCurve { get; set; } = 2.5f;
    /// <summary>Hands off, it slows by coast_decel (m/s²) plus coast_drag (per second) times its speed. Higher: it stops sooner when you lift; lower: it rolls on, heavier.</summary>
    public float CoastDecel { get; set; } = 1.3f;
    public float CoastDrag { get; set; } = 0.07f;
    /// <summary>The share of the brake that bites the moment the key goes against the travel (0 to 1); the rest comes in with the throttle ramp. Higher: a tap brakes harder.</summary>
    public float BrakeBite { get; set; } = 0.55f;
    /// <summary>Reverse top speed as a fraction of the forward one.</summary>
    public float ReverseTopFraction { get; set; } = 0.3f;
    /// <summary>Sideways acceleration the tyres hold, m/s². It caps the yaw rate at speed (grip / speed), so the circle at top speed is top_speed² / grip. Higher: tighter at speed.</summary>
    public float Grip { get; set; } = 8.5f;
    /// <summary>How fast the yaw rate follows the wheel, per second. Higher: it turns in at once; lower: it is lazier, heavier.</summary>
    public float YawResponse { get; set; } = 9;
    /// <summary>Handbrake (Jump): its deceleration (m/s²), the share of the grip the tyres keep sideways while it is on (lower: the tail slides wider), and how much sharper it steers.</summary>
    public float HandbrakeDecel { get; set; } = 9;
    public float HandbrakeGrip { get; set; } = 0.3f;
    public float HandbrakeSteer { get; set; } = 1.3f;
    /// <summary>A ledge up to this high (m) is driven over as a kerb; a higher one within one tick's travel is a wall. Slopes up to max_climb_deg are climbed.</summary>
    public float StepHeight { get; set; } = 0.4f;
    public float MaxClimbDeg { get; set; } = 48;
    /// <summary>A drop up to this deep (m) keeps the wheels on the ground; a deeper one and it is in the air until it lands.</summary>
    public float SnapDown { get; set; } = 0.3f;
}

/// <summary>The drive afloat (vehicles.json "drive.water").</summary>
public class DriveWaterDef
{
    /// <summary>How deep it sits: the origin (the wheels' underside) rides this far under the surface, metres. The model's waterline.</summary>
    public float Draft { get; set; } = 0.56f;
    /// <summary>It floats where the water over the ground is at least this deep (m); in less it stands on its wheels. (Half of it, to go aground again.)</summary>
    public float FloatDepth { get; set; } = 0.12f;
    /// <summary>Thrust astern as a share of the thrust ahead: there are no brakes afloat, this is how it stops. Higher: it stops shorter and backs faster.</summary>
    public float ReverseThrust { get; set; } = 0.6f;
    /// <summary>A little drag in proportion to the speed, per second, on top of the hull's own (which grows with the square). Higher: it carries less way when you lift off.</summary>
    public float CoastDrag { get; set; } = 0.12f;
    /// <summary>The circle it turns at speed, metres (yaw rate = speed / turn_radius).</summary>
    public float TurnRadius { get; set; } = 16;
    /// <summary>Yaw rate the propeller's wash gives at full throttle whatever the speed, rad/s: it turns from a standstill under power. Astern it turns the other way.</summary>
    public float PropSteer { get; set; } = 0.15f;
    /// <summary>How fast the yaw rate follows the helm, per second. Lower: lazier, more boat.</summary>
    public float YawResponse { get; set; } = 1.6f;
    /// <summary>How fast the water takes the sideways speed away, per second. Lower: it slides wider out of a turn.</summary>
    public float SideDrag { get; set; } = 1.5f;
    /// <summary>How far ahead of the centre it pivots, metres: turning, the stern swings out round that point. 0: it turns about its middle.</summary>
    public float SternSwing { get; set; } = 1.2f;
    /// <summary>The hull's rise and fall to its waterline: how fast (rad/s) and how damped (1: no overshoot; lower: it plunges in, comes up and settles).</summary>
    public float BuoyancyResponse { get; set; } = 5;
    public float BuoyancyDamping { get; set; } = 0.4f;
    /// <summary>Climbing out, the wheels lift it onto the bank at this rate, m/s (the heave). Higher: it pops out; lower: it hauls itself out.</summary>
    public float ClimbOutRate { get; set; } = 1.4f;
}

/// <summary>
/// How the body moves over its wheels and on the water (vehicles.json "drive.body"). Drawn by every client from the vehicle's motion
/// (<see cref="DriveBody"/>); nothing here changes where the vehicle goes or who is hit.
/// </summary>
public class DriveBodyDef
{
    /// <summary>Degrees the tail squats per m/s² of acceleration, the nose dives per m/s² of braking, and the body leans out of a turn per m/s² sideways.</summary>
    public float SquatDeg { get; set; } = 0.55f;
    public float DiveDeg { get; set; } = 0.6f;
    public float LeanDeg { get; set; } = 0.55f;
    /// <summary>The most it pitches and rolls on its springs, degrees.</summary>
    public float MaxPitchDeg { get; set; } = 5;
    public float MaxRollDeg { get; set; } = 6;
    /// <summary>The suspension: how fast it answers (rad/s) and how damped (1: no overshoot; lower: it rocks once before it settles, which reads as weight).</summary>
    public float Response { get; set; } = 7;
    public float Damping { get; set; } = 0.45f;
    /// <summary>How fast the drawn body takes up the slope of the ground under the wheels, per second. Lower: smoother over rough ground.</summary>
    public float TerrainResponse { get; set; } = 12;
    /// <summary>Afloat: degrees the bow lifts at full throttle and top water speed, and degrees it heels out of a turn per m/s² sideways.</summary>
    public float BowRiseDeg { get; set; } = 5;
    public float WaterLeanDeg { get; set; } = 1.2f;
    /// <summary>Afloat: how fast the hull answers (rad/s) and how damped. Slow and loose, so it wallows.</summary>
    public float WaterResponse { get; set; } = 2.5f;
    public float WaterDamping { get; set; } = 0.35f;
    /// <summary>The bob at rest: metres up and down, degrees of pitch and roll, and its period in seconds.</summary>
    public float BobHeight { get; set; } = 0.03f;
    public float BobPitchDeg { get; set; } = 1.0f;
    public float BobRollDeg { get; set; } = 1.5f;
    public float BobPeriod { get; set; } = 2.6f;
    /// <summary>How many times larger the bob is just after it has gone in or been driven hard, and the seconds it takes to settle back.</summary>
    public float BobDisturbed { get; set; } = 3;
    public float SettleTime { get; set; } = 3;
    /// <summary>The front wheels' largest steering angle as drawn, degrees.</summary>
    public float WheelLockDeg { get; set; } = 30;
    /// <summary>The propeller's speed at full throttle, turns a second.</summary>
    public float PropellerRps { get; set; } = 9;

    public string? Validate()
    {
        if (Response <= 0 || Damping <= 0 || TerrainResponse <= 0 || WaterResponse <= 0 || WaterDamping <= 0 || BobPeriod <= 0 || SettleTime <= 0) return "body: responses, dampings, bob_period and settle_time must be positive";
        if (SquatDeg < 0 || DiveDeg < 0 || LeanDeg < 0 || BowRiseDeg < 0 || WaterLeanDeg < 0 || BobHeight < 0 || BobPitchDeg < 0 || BobRollDeg < 0 || BobDisturbed < 1 || PropellerRps < 0)
            return "body: gains and bob sizes must not be negative, bob_disturbed at least 1";
        if (MaxPitchDeg is < 0 or > 30 || MaxRollDeg is < 0 or > 30 || WheelLockDeg is < 0 or > 60) return "body: max_pitch_deg and max_roll_deg are 0..30, wheel_lock_deg 0..60";
        return null;
    }
}

/// <summary>
/// The driver's chase camera of a predicted vehicle (vehicles.json "drive.camera"): the helicopter's spring camera (<see cref="FlightCameraDef"/>),
/// and it swings round with the vehicle, since the keys steer and the mouse only looks.
/// </summary>
public class DriveCameraDef : FlightCameraDef
{
    public DriveCameraDef() { Distance = 7.5f; Height = 2.2f; Follow = 10; MaxLag = 1.6f; Tilt = 0.35f; FovBoostDeg = 7; }

    /// <summary>The share of the vehicle's turning the view takes (0 to 1). 1: the camera stays behind it; 0: the view keeps its compass heading, as on the other cars.</summary>
    public float TurnFollow { get; set; } = 1;
    /// <summary>How fast the view catches up with a turn, per second. Lower: the vehicle swings in the frame first, then the camera comes round.</summary>
    public float TurnResponse { get; set; } = 5;

    public new string? Validate() =>
        base.Validate() ?? (TurnFollow is < 0 or > 1 || TurnResponse <= 0 ? "camera: turn_follow 0..1, turn_response positive" : null);
}

/// <summary>
/// vehicles.json "lights" per type: lamp positions [x, y, z] in the vehicle frame (forward -Z, origin on the ground, as the hood).
/// A boat's bow light is just another "head" point. Client and server only read it; the rules are in <see cref="Headlights"/>.
/// </summary>
public class VehicleLights
{
    /// <summary>Headlamp positions, 1 to 4 (a pair for a car). Each gets a beam while the lights are on.</summary>
    public List<float[]> Head { get; set; } = new();
    /// <summary>Tail lamp positions, 0 to 4 (red; brighter while braking).</summary>
    public List<float[]> Tail { get; set; } = new();
    /// <summary>Beam reach, metres.</summary>
    public float BeamRange { get; set; } = 30;
    /// <summary>Beam half angle (the cone's outer edge), degrees.</summary>
    public float BeamAngle { get; set; } = 28;
}

/// <summary>A destroyed vehicle's blast (vehicles.json "explosion"): splash numbers like a weapon's, in absolute damage.</summary>
public class VehicleExplosionDef
{
    /// <summary>Damage at the centre (inside the splash's full-damage radius); falls off linearly to the splash's edge damage.</summary>
    public float Damage { get; set; } = 110;
    /// <summary>Damage to whoever is still in the seat: the driver does not survive (a spawn-protected one is thrown out instead).</summary>
    public float DriverDamage { get; set; } = 1000;
    public SplashDef Splash { get; set; } = new() { Radius = 9, FullDamageRadius = 3, EdgeDamage = 25, SelfDamageMultiplier = 0.5f, KnockbackSpeed = 6 };
    /// <summary>Other vehicles take the splash damage times this.</summary>
    public float VehicleDamageMultiplier { get; set; } = 1.5f;
    /// <summary>A vehicle destroyed by another's blast goes off this many seconds later (chain reaction, each vehicle only once).</summary>
    public float ChainDelay { get; set; } = 0.25f;
}

/// <summary>
/// The reusable driving parameter set (vehicles.json "handling" per type; defaults here are the shared feel). A later type
/// (jeep, quad, boat) reuses the model by data: only the keys it changes need to be written.
/// </summary>
public class VehicleHandling
{
    /// <summary>Rolling resistance with no input, m/s².</summary>
    public float CoastDecel { get; set; } = 1.5f;
    /// <summary>Deceleration on the handbrake (Jump), m/s².</summary>
    public float HandbrakeDecel { get; set; } = 12;
    /// <summary>Reverse top speed as a fraction of the forward top speed.</summary>
    public float ReverseTopFraction { get; set; } = 0.2f;
    /// <summary>How fast the yaw rate follows the steering target, rad/s². Real steering takes a fraction of a second to lock.</summary>
    public float YawSlew { get; set; } = 8;
    /// <summary>Extra thrust from a standstill: acceleration is multiplied by this at 0 m/s and fades linearly to 1 at <see cref="LaunchSpeed"/>, so the first couple of seconds are punchy while the top end stays as it was. 1 turns it off.</summary>
    public float LaunchBoost { get; set; } = 2.5f;
    /// <summary>Speed (m/s) at which the <see cref="LaunchBoost"/> has faded out.</summary>
    public float LaunchSpeed { get; set; } = 12;
    /// <summary>Steering sharpening while the handbrake is held.</summary>
    public float HandbrakeSteerBoost { get; set; } = 1.5f;
}

public class VehicleDef
{
    public string Id { get; set; } = "";
    public float TopSpeed { get; set; }
    /// <summary>Forward acceleration, m/s² (linear up to top speed). 0-100 km/h takes 27.8 / Acceleration seconds.</summary>
    public float Acceleration { get; set; }
    /// <summary>Braking deceleration against the direction of travel, m/s².</summary>
    public float Brake { get; set; } = 9;
    /// <summary>Tightest circle at low speed (the steering lock), metres. Turning circle diameter is twice this.</summary>
    public float TurnRadius { get; set; } = 5.5f;
    /// <summary>Lateral acceleration the tyres (or the lean) hold, m/s². No longer shapes the steering (see <see cref="HighSpeedRadiusFactor"/>); kept for the data files.</summary>
    public float MaxLateralAccel { get; set; } = 8;
    /// <summary>How many times wider the turn circle is at top speed than at a standstill (1 = same circle at every speed). Widens linearly with speed in between.</summary>
    public float HighSpeedRadiusFactor { get; set; } = 6;
    /// <summary>Shared driving knobs (coast, handbrake, reverse, steering slew). Overridable per type with a "handling" block.</summary>
    public VehicleHandling Handling { get; set; } = new();
    public float Mass { get; set; }
    /// <summary>Hidden hit points; the damage stages are fractions of this (vehicles.json damage_stages).</summary>
    public float Hp { get; set; } = 300;
    /// <summary>Where the smoke and sparks come out, [x, y, z] in the vehicle's frame (forward is -Z).</summary>
    public float[] Hood { get; set; } = { 0, 0.95f, -1.3f };
    /// <summary>How many of this type are placed at match start.</summary>
    public int SpawnCount { get; set; }
    /// <summary>Half extents of the footprint (server collision box and run-over test), metres.</summary>
    public float HalfWidth { get; set; } = 1.1f;
    public float HalfLength { get; set; } = 2.15f;
    /// <summary>Chase camera distance behind the vehicle, metres (#218). Null: derived from the length, see <see cref="ChaseCamera.Wanted"/>.</summary>
    public float? ChaseDistance { get; set; }
    /// <summary>Chase camera height above the vehicle's origin, metres. Null: derived from the length.</summary>
    public float? ChaseHeight { get; set; }
    /// <summary>
    /// Height of the hit volume, metres from the ground: the top of the visible body (roof, cabin, rider's head). A bullet that passes over
    /// it misses; one that enters the footprint below it hits. See <see cref="VehicleBox"/>.
    /// </summary>
    public float Height { get; set; } = 1.2f;
    /// <summary>Override of the file's enter_radius for this type (metres from the footprint). Null: the file's.</summary>
    public float? EnterRadius { get; set; }
    /// <summary>Seats in order; seat 0 is the driver. Default: one driver seat.</summary>
    public List<VehicleSeatDef> Seats { get; set; } = new() { new VehicleSeatDef { Role = "driver" } };
    /// <summary>The gun a gunner seat fires. Null: no gun.</summary>
    public VehicleGunDef? Gun { get; set; }
    /// <summary>
    /// The gunner sits in the open (the flak truck's turret): shots that reach his body hit him, not the hull around him (<see cref="GunnerCover"/>).
    /// False keeps every rider behind the hull, as the jeep's roof gunner is.
    /// </summary>
    public bool ExposedGunner { get; set; }
    /// <summary>
    /// Whether the hull leaves the rider of <paramref name="seat"/> uncovered: the seat says so itself (<see cref="VehicleSeatDef.Exposed"/>, the
    /// amphibious transport's three), or it is the gunner seat of a vehicle with <see cref="ExposedGunner"/> (the flak truck, the utility helicopter).
    /// </summary>
    public bool SeatExposed(int seat) => seat >= 0 && seat < Seats.Count && (Seats[seat].Exposed || ExposedGunner && Seats[seat].Role == "gunner");
    /// <summary>An anti-aircraft turret worked from the gunner seat (the flak truck). Null: none. Excludes <see cref="Gun"/>.</summary>
    public VehicleFlakDef? Flak { get; set; }
    /// <summary>
    /// Where the <see cref="Gun"/> is mounted and how far it swings (the utility helicopter's door gun). Null: the gun fires along the gunner's
    /// view from above the seat, as the jeep's does. With it, the gun is on a pintle and the aim is held inside its arc (<see cref="DoorGunRules"/>).
    /// </summary>
    public VehicleDoorGunDef? DoorGun { get; set; }
    /// <summary>Mass (kg) for run-over damage. Unset: the v1 run-over numbers (see <see cref="VehiclesFile.RunOverScale"/>).</summary>
    public float? RunOverMass { get; set; }
    /// <summary>
    /// Where the type travels (<see cref="VehicleMedium"/>): "land" (default), "amphibious" (drives on land, floats on water at
    /// <see cref="WaterTopSpeed"/>), "boat" (water only: on land it coasts to a stop at <see cref="LandDecel"/>) or "air" (it flies:
    /// the utility helicopter, stepped by <see cref="FlightModel"/> with the numbers of <see cref="Flight"/>).
    /// </summary>
    public string Medium { get; set; } = VehicleMedium.Land;
    /// <summary>Amphibious only: top speed afloat, m/s.</summary>
    public float WaterTopSpeed { get; set; } = 7;
    /// <summary>Amphibious only: forward acceleration afloat, m/s².</summary>
    public float WaterAcceleration { get; set; } = 1.5f;
    /// <summary>Boat only: below this speed (m/s) the hull is still pushing water aside and the boat accelerates at half rate; above it it planes. 0: no planing.</summary>
    public float PlaningSpeed { get; set; }
    /// <summary>Boat only: deceleration on land, m/s², while it coasts to a stop (it has no drive there).</summary>
    public float LandDecel { get; set; } = 5;
    /// <summary>Whether a driver running someone over hurts them. Land types, and an amphibious only on land; a boat never runs anyone over.</summary>
    public bool RunOver { get; set; } = true;
    /// <summary>Whether a wall impact hurts the vehicle. False for a boat: running aground is not damage.</summary>
    public bool CrashDamage { get; set; } = true;
    /// <summary>True for a type that can float (amphibious or boat): its origin rides the water surface when it is over a water body.</summary>
    public bool CanFloat => Medium is VehicleMedium.Amphibious or VehicleMedium.Boat;
    /// <summary>The flight handling of a type with medium "air" (vehicles.json "flight"); null for everything that drives or floats.</summary>
    public VehicleFlightDef? Flight { get; set; }
    /// <summary>
    /// The handling of a type its driver's client predicts (vehicles.json "drive", the amphibious transport, ADR 0010): it is stepped by
    /// <see cref="DriveModel"/> and never by <see cref="CarModel"/>. Null for every other type.
    /// </summary>
    public VehicleDriveDef? Drive { get; set; }
    /// <summary>A single fixed body colour [r, g, b] (sRGB) for a type with one look (olive drab). Replaces the colour variants: such a type has one variant.</summary>
    public float[]? Tint { get; set; }
    /// <summary>Model file relative to assets/ (empty: the code-built car with the Kenney sedan). Scale, yaw and offset fit the raw node to the game (forward -Z, origin at the ground centre).</summary>
    public string Model { get; set; } = "";
    public float ModelScale { get; set; } = 1;
    public float ModelYawDeg { get; set; }
    /// <summary>Headlights and tail lights (vehicles.json "lights"); null for a type without lamps.</summary>
    public VehicleLights? Lights { get; set; }
    /// <summary>The glass panes of this type (vehicles.json "glass"), at most 24; empty for a type without glass. A pane's place in the list is its index on the wire.</summary>
    public List<VehicleGlassDef> Glass { get; set; } = new();
    List<GlassPaneRect>? _glassRects;
    /// <summary>The panes as rectangles in the vehicle's frame, in the order of <see cref="Glass"/> (built once).</summary>
    public IReadOnlyList<GlassPaneRect> GlassRects => _glassRects ??= Glass.Select(g => g.Rect()).ToList();
    /// <summary>
    /// The places where the body of this type is open (vehicles.json "open_sides": the helicopter's cabin has no doors), written as glass panes are
    /// (centre, outward normal, size, an optional outline; hp is not read). A bullet gets in and out through an open side as through a pane
    /// (<see cref="GlassRules.Through"/>); it has no state and nothing is drawn for it. Written by hand, not by the model script.
    /// </summary>
    public List<VehicleGlassDef> OpenSides { get; set; } = new();
    List<GlassPaneRect>? _openSideRects;
    /// <summary>The open sides as rectangles in the vehicle's frame (built once).</summary>
    public IReadOnlyList<GlassPaneRect> OpenSideRects => _openSideRects ??= OpenSides.Select(g => g.Rect()).ToList();
    /// <summary>Offset applied to the model node before scale, as [x, y, z].</summary>
    public float[] ModelOffset { get; set; } = { 0, 0, 0 };
    /// <summary>The model's own material that takes the colour variants, and the model's own materials whose colours the variants are (index = colour byte).</summary>
    public string PaintMaterial { get; set; } = "";
    public List<string> Paints { get; set; } = new();
    /// <summary>Number of colour variants: the model's paints, or the shared car palette.</summary>
    public int Variants => Tint != null ? 1 : Paints.Count > 0 ? Paints.Count : VehicleColours.Palette.Length;
}

using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Glass;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Vehicles;

/// <summary>vehicles.json. Rules from the vehicles ticket (#77); driving numbers are placeholders tuned by driving.</summary>
public class VehiclesFile : IVersioned
{
    public int Version { get; set; }
    public float RunOverMinSpeed { get; set; } = 8;
    public float RunOverDamagePerSpeed { get; set; } = 10;
    public float RunOverMaxDamage { get; set; } = 100;
    /// <summary>Remaining-HP fractions at which smoke stage 1, 2 and 3 start (descending); at 0 HP the vehicle explodes.</summary>
    public List<float> DamageStages { get; set; } = new() { 0.66f, 0.33f, 0.12f };
    /// <summary>Seconds a destroyed vehicle stays gone before it returns to its origin with full HP.</summary>
    public float DestroyedReturnTime { get; set; } = 30;
    /// <summary>
    /// Any collision (a wall, a tree, a ledge, a hard landing, another vehicle, #417): speed lost in one tick above the minimum (m/s) hurts the
    /// vehicle by this much per m/s. Below the minimum a bump costs nothing. See <see cref="CrashDamage"/>.
    /// </summary>
    public float CrashMinSpeedLoss { get; set; } = 13.9f;
    public float CrashDamagePerSpeed { get; set; } = 8;
    /// <summary>A collision that took at least this much speed in one tick (m/s) is heard (#410); from <see cref="CrashMinSpeedLoss"/> it is the hard crash. See <see cref="CrashSoundOf"/>.</summary>
    public float CrashSoundMinSpeedLoss { get; set; } = 4;
    /// <summary>Seconds before the same vehicle's next crash sound: a hull that grinds along a wall is not a crash every tick.</summary>
    public float CrashSoundGap { get; set; } = 0.5f;
    /// <summary>What a collision that took <paramref name="speedLoss"/> m/s sounds like: nothing under crash_sound_min_speed_loss, the hard crash where the hull is hurt (#410).</summary>
    public CrashSound CrashSoundOf(float speedLoss) => speedLoss > CrashMinSpeedLoss ? CrashSound.Hard : speedLoss >= CrashSoundMinSpeedLoss ? CrashSound.Light : CrashSound.None;
    /// <summary>Hit points a collision that took <paramref name="speedLoss"/> m/s costs a vehicle: nothing up to <see cref="CrashMinSpeedLoss"/>. The one rule every collision goes through (#417).</summary>
    public float CrashDamage(float speedLoss) => MathF.Max(0, speedLoss - CrashMinSpeedLoss) * CrashDamagePerSpeed;
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
            ValidateBasics(v);
            ValidateLook(v);
            ValidateTowing(v);
            ValidateSeats(v);
            ValidateGuns(v);
            ValidateSize(f, v);
            ValidateDoorGun(v);
            ValidateMotion(v);
            if (v.CargoVolume?.Problem(v) is { } cargo) throw new DataFileException($"vehicles.json: {v.Id}: cargo_volume: {cargo}");
        }
        if (f.DamageStages.Count != 3 || f.DamageStages.Any(x => x <= 0 || x >= 1) || f.DamageStages[0] <= f.DamageStages[1] || f.DamageStages[1] <= f.DamageStages[2])
            throw new DataFileException("vehicles.json: damage_stages are three descending fractions between 0 and 1");
        if (f.Explosion.Splash.Radius <= 0) throw new DataFileException("vehicles.json: explosion.splash.radius must be positive");
        if (f.BumpRestitution is < 0 or > 1) throw new DataFileException("vehicles.json: bump_restitution is between 0 and 1");
        if (f.RunOverReferenceMass <= 0) throw new DataFileException("vehicles.json: run_over_reference_mass must be positive");
        return f;
    }

    /// <summary>Hit points, the driving numbers, spawning, and the model, paints and skins.</summary>
    static void ValidateBasics(VehicleDef v)
    {
        if (v.Hp <= 0) throw new DataFileException($"vehicles.json: {v.Id}: hp must be positive");
        if (v.TopSpeed <= 0 || v.Acceleration <= 0 || v.Brake <= 0 || v.TurnRadius <= 0 || v.MaxLateralAccel <= 0)
            throw new DataFileException($"vehicles.json: {v.Id}: top_speed, acceleration, brake, turn_radius and max_lateral_accel must be positive");
        if (v.HighSpeedRadiusFactor < 1) throw new DataFileException($"vehicles.json: {v.Id}: high_speed_radius_factor must be at least 1");
        if (v.SpawnCount < 0 || v.FixedSpots.Count > 8 || v.FixedSpots.Any(sp => sp is null || !float.IsFinite(sp.X) || !float.IsFinite(sp.Z) || sp.YawDeg is { } y && !float.IsFinite(y)))
            throw new DataFileException($"vehicles.json: {v.Id}: spawn_count must not be negative; fixed_spots are at most 8 of {{ x, z, optional yaw_deg }}");
        if (v.Hood.Length != 3) throw new DataFileException($"vehicles.json: {v.Id}: hood is [x, y, z]");
        if (v.Variants is < 1 or > 16) throw new DataFileException($"vehicles.json: {v.Id}: variants must be 1..16");
        if (v.Paints.Count > 0 && v.PaintMaterial == "") throw new DataFileException($"vehicles.json: {v.Id}: paints need paint_material");
        if (v.Skins.Count > 0 && (v.Skins[0] != v.Model || v.Skins.Any(k => !k.StartsWith("pack:", StringComparison.Ordinal)) || v.Paints.Count > 0 || v.Tint != null))
            throw new DataFileException($"vehicles.json: {v.Id}: skins are pack: models, the first is the model, and they exclude paints and tint");
    }

    /// <summary>Lamps, sounds, glass panes and open sides, tint.</summary>
    static void ValidateLook(VehicleDef v)
    {
        if (v.Lights is { } l)
        {
            if (l.Head.Count is < 1 or > 4 || l.Tail.Count > 4) throw new DataFileException($"vehicles.json: {v.Id}: lights need 1 to 4 head and 0 to 4 tail lamps");
            if (l.Head.Concat(l.Tail).Any(p => p is null || p.Length != 3)) throw new DataFileException($"vehicles.json: {v.Id}: each lamp is [x, y, z]");
            if (l.BeamRange <= 0 || l.BeamAngle <= 0 || l.BeamAngle >= 89) throw new DataFileException($"vehicles.json: {v.Id}: beam_range must be positive, beam_angle 1..88 degrees");
        }
        if (v.Sounds is { } snd) VehicleSoundsDef.Validate(v.Id, snd);
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
    }

    /// <summary>A trailer, a launcher, a tow hook.</summary>
    static void ValidateTowing(VehicleDef v)
    {
        if (v.Trailer is { } trl)
        {
            if (v.Seats.Count != 0) throw new DataFileException($"vehicles.json: {v.Id}: a trailer has no seats (\"seats\": [])");
            if (v.Tow != null) throw new DataFileException($"vehicles.json: {v.Id}: a trailer does not tow: tow and trailer exclude each other");
            if (v.Medium != VehicleMedium.Land || v.Drive != null || v.Gun != null || v.Flak != null || v.DoorGun != null) throw new DataFileException($"vehicles.json: {v.Id}: a trailer is a land type with no drive, gun, flak or door_gun block");
            if (trl.Validate() is { } why) throw new DataFileException($"vehicles.json: {v.Id}: trailer: {why}");
        }
        else if (v.Seats.Count is < 1 or > VehicleSeat.MaxSeats || v.Seats[0].Role != "driver") throw new DataFileException($"vehicles.json: {v.Id}: seats are 1..{VehicleSeat.MaxSeats} and the first is the driver");
        if (v.Launcher is { } lch && lch.Validate(v) is { } lwhy) throw new DataFileException($"vehicles.json: {v.Id}: launcher: {lwhy}");
        if (v.Tow is { } tw)
        {
            if (v.Medium == VehicleMedium.Air) throw new DataFileException($"vehicles.json: {v.Id}: a tow block is for a land or amphibious vehicle");
            if (tw.Validate() is { } why) throw new DataFileException($"vehicles.json: {v.Id}: tow: {why}");
        }
    }

    /// <summary>The seats, and the gunner the exposed_gunner flag needs.</summary>
    static void ValidateSeats(VehicleDef v)
    {
        foreach (var seat in v.Seats)
        {
            if (seat.Role is not ("driver" or "gunner" or "passenger")) throw new DataFileException($"vehicles.json: {v.Id}: seat role {seat.Role} is not driver, gunner or passenger");
            if (seat.Role == "driver" && v.Seats.Count(x => x.Role == "driver") != 1) throw new DataFileException($"vehicles.json: {v.Id}: one driver seat");
            if (seat.Offset.Length != 3) throw new DataFileException($"vehicles.json: {v.Id}: seat offset is [x, y, z]");
            if (seat.Armed && seat.Role != "passenger") throw new DataFileException($"vehicles.json: {v.Id}: only a passenger seat can be armed (a driver drives, a gunner has the vehicle's gun)");
            if (seat.Standing && !seat.Exposed) throw new DataFileException($"vehicles.json: {v.Id}: a standing seat is an exposed seat");
            if (seat.EyeHeight is { } eh && !(eh is >= 0.3f and <= 2.2f)) throw new DataFileException($"vehicles.json: {v.Id}: a seat's eye_height is 0.3..2.2 m above its feet");
            if (seat.Aim is { } aim)
            {
                if (!seat.Armed) throw new DataFileException($"vehicles.json: {v.Id}: a seat's aim is for an armed seat");
                if (aim.ArcDeg is <= 0 or > 180 || aim.MinPitchDeg < -89 || aim.MaxPitchDeg > 89 || aim.MinPitchDeg >= aim.MaxPitchDeg || aim.SpreadFullSpeed <= 0)
                    throw new DataFileException($"vehicles.json: {v.Id}: a seat's aim needs arc_deg 0..180, min_pitch_deg below max_pitch_deg (within -89..89) and a positive spread_full_speed");
            }
        }
        for (var si = 0; si < v.Seats.Count; si++)
            if (v.ExposedGunner && v.Seats[si].Role == "gunner" && v.Seats[si].Cover is { } gc && gc != SeatCover.Exposed)
                throw new DataFileException($"vehicles.json: {v.Id}: exposed_gunner makes the gunner seat exposed, its cover says {gc}");
        if (v.ExposedGunner && !v.Seats.Any(x => x.Role == "gunner")) throw new DataFileException($"vehicles.json: {v.Id}: exposed_gunner needs a gunner seat");
    }

    /// <summary>The mounted gun and the flak turret.</summary>
    static void ValidateGuns(VehicleDef v)
    {
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
    }

    /// <summary>Chase camera, footprint, hull boxes, enter radius, medium and floating.</summary>
    static void ValidateSize(VehiclesFile f, VehicleDef v)
    {
        if (v.ChaseDistance is <= 0 || v.ChaseHeight is < 0) throw new DataFileException($"vehicles.json: {v.Id}: chase_distance must be positive and chase_height not negative");
        if (v.Height <= 0 || v.HalfWidth <= 0 || v.HalfLength <= 0) throw new DataFileException($"vehicles.json: {v.Id}: height, half_width and half_length must be positive");
        if (v.Hull is { } hull && (hull.Count == 0 || hull.Any(b => b == null || b.Length != 6 || !(b[2] > 0) || !(b[3] > 0) || !(b[5] > b[4]) || b.Any(x => !float.IsFinite(x)))))
            throw new DataFileException($"vehicles.json: {v.Id}: hull is a list of [cx, cz, half_width, half_length, y0, y1] with positive half sizes and y1 above y0");
        if (v.Hull != null && v.Flight == null && v.Trailer == null) throw new DataFileException($"vehicles.json: {v.Id}: a hull list is for a flying type or a trailer; a driven ground vehicle gets one box from half_width, half_length and height, tilted by its slope (#483)");
        if (v.EnterRadius is < 0 || f.EnterRadius < 0) throw new DataFileException($"vehicles.json: {v.Id}: enter_radius must not be negative");
        if (v.RunOverMass is { } rm && rm <= 0) throw new DataFileException($"vehicles.json: {v.Id}: run_over_mass must be positive");
        if (!VehicleMedium.All.Contains(v.Medium)) throw new DataFileException($"vehicles.json: {v.Id}: medium {v.Medium} is not land, amphibious or air");
        if (v.CanFloat && (v.WaterTopSpeed <= 0 || v.WaterAcceleration <= 0))
            throw new DataFileException($"vehicles.json: {v.Id}: water_top_speed and water_acceleration must be positive");
    }

    /// <summary>The pintle or ring gun.</summary>
    static void ValidateDoorGun(VehicleDef v)
    {
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
    }

    /// <summary>The drive and flight blocks.</summary>
    static void ValidateMotion(VehicleDef v)
    {
        if (v.Drive is { } dr)
        {
            if (v.Medium is not (VehicleMedium.Amphibious or VehicleMedium.Land)) throw new DataFileException($"vehicles.json: {v.Id}: a drive block is for a land or amphibious vehicle");
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

    /// <summary>Run-over damage multiplier of a type: its run_over_mass over the reference mass, or 1 when it has none (the v1 numbers).</summary>
    public float EnterRadiusOf(VehicleDef v) => v.EnterRadius ?? EnterRadius;

    public float RunOverScale(VehicleDef v) => v.RunOverMass is { } m ? m / RunOverReferenceMass : 1f;
}

/// <summary>vehicles.json "medium" values (see <see cref="VehicleDef.Medium"/>).</summary>
public static class VehicleMedium
{
    public const string Land = "land", Amphibious = "amphibious", Air = "air";
    public static readonly string[] All = { Land, Amphibious, Air };
}

/// <summary>
/// A fixed place for one vehicle (vehicles.json "fixed_spots"): world ground position (x east, z south, as the game's world) and, optionally, a yaw.
/// Without <see cref="YawDeg"/> the vehicle faces along the road nearest to the spot (<see cref="RoadHeading"/>), or yaw 0 when there is none close.
/// </summary>
public class VehicleSpotDef
{
    public float X { get; set; }
    public float Z { get; set; }
    /// <summary>Heading in degrees, game convention (0 faces -Z, positive turns left). Null: along the nearest road.</summary>
    public float? YawDeg { get; set; }
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
    /// <summary>Hit points of this pane; null takes its <see cref="Kind"/>'s from glass.json (<see cref="GlassConfig.PaneHp"/>).</summary>
    public float? Hp { get; set; }
    /// <summary>How the pane treats a bullet (ADR 0035): Normal (the default) lets it through at once, Reinforced stops it while it holds.</summary>
    public GlassKind Kind { get; set; } = GlassKind.Normal;

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

/// <summary>The kind of a vehicle's glass pane (vehicles.json glass <c>kind</c>, the table in glass.json <c>kinds</c>, ADR 0035).</summary>
public enum GlassKind
{
    /// <summary>A bullet damages the pane and goes on, full damage to what is behind it.</summary>
    Normal,
    /// <summary>More hit points; while it holds it stops a bullet (the pane takes the damage, whatever is behind none), once shattered a shot goes through.</summary>
    Reinforced,
}

/// <summary>
/// vehicles.json "lights" per type: lamp positions [x, y, z] in the vehicle frame (forward -Z, origin on the ground, as the hood).
/// Client and server only read it; the rules are in <see cref="Headlights"/>.
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
/// (truck, quad) reuses the model by data: only the keys it changes need to be written.
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

/// <summary>vehicles.json "launcher": the Scud truck's launcher (<see cref="ScudLauncher"/>). Offsets are in the vehicle's frame (x right, y up, forward is -Z, origin on the ground at the centre).</summary>
public class VehicleLauncherDef
{
    /// <summary>The seat whose occupant is the operator (the targeting screen is theirs); the others only watch and confirm.</summary>
    public int OperatorSeat { get; set; } = 1;
    /// <summary>Where the missile entity spawns: the centre of the ERECTED rocket on the raised rack (the model's <c>missile</c> node with the rack at 90 degrees), so it leaves from where it stands.</summary>
    public float[] Muzzle { get; set; } = { 0, 5.89f, 4.111f };
    /// <summary>Where the launch smoke and the ignition flame come from (the erected rocket's tail end).</summary>
    public float[] Exhaust { get; set; } = { 0, 1.1f, 4.111f };
    /// <summary>The wheels' radius, metres: they roll by the distance driven (the model's wheel centres are at this height).</summary>
    public float WheelRadius { get; set; } = 0.736f;
    /// <summary>
    /// The steered axles, front first: the distance from the middle of the fixed axles to each steered axle, metres. An axle's wheels turn by atan(arm / turn radius) (the truck turns about a point on the fixed axles' line),
    /// so the front axle turns more than the second. Empty: no wheel steers.
    /// </summary>
    public float[] SteerArms { get; set; } = { 6.431f, 4.272f };

    /// <summary>Why the block is bad, or null.</summary>
    public string? Validate(VehicleDef v)
    {
        if (OperatorSeat < 1 || OperatorSeat >= v.Seats.Count) return "operator_seat must be a seat of the vehicle other than the driver's";
        if (Muzzle.Length != 3 || Exhaust.Length != 3 || Muzzle.Concat(Exhaust).Any(x => !float.IsFinite(x))) return "muzzle and exhaust are [x, y, z]";
        if (!(WheelRadius > 0.1f && WheelRadius < 2f)) return "wheel_radius must be between 0.1 and 2 metres";
        if (SteerArms.Any(a => !(a > 0.5f && a < 20f))) return "steer_arms are distances in metres between 0.5 and 20";
        if (v.Flight != null || v.Gun != null || v.Flak != null || v.DoorGun != null) return "a launcher truck is a ground vehicle with no gun, flak or door gun";
        return null;
    }
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
    /// <summary>How many of this type are dealt at random over the spawn areas at match start. 0 for a type that only stands at its <see cref="FixedSpots"/>.</summary>
    public int SpawnCount { get; set; }
    /// <summary>
    /// Places this type always stands at (vehicles.json "fixed_spots"), one vehicle each, in addition to the <see cref="SpawnCount"/> dealt at random.
    /// A vehicle that returns (destroyed, or out of the world; an abandoned one stays) comes back to its spot. See <see cref="VehicleSpotDef"/>.
    /// </summary>
    public List<VehicleSpotDef> FixedSpots { get; set; } = new();
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
    /// <summary>
    /// The hull as several boxes instead of the one upright box over the footprint (<see cref="HalfWidth"/>, <see cref="HalfLength"/>, <see cref="Height"/>): [cx, cz, half_width, half_length, y0, y1] each, in the
    /// vehicle's node frame, metres (the utility helicopter: cabin, tail boom, fin). They are both Hit shape and Body shape (ADR 0028) and tilt with the vehicle's pitch and roll (an aircraft's flight state, a trailer's ground fit, <see cref="TrailerAttitude"/>, a driven vehicle's slope; vehicle-to-vehicle contact of a trailer keeps the footprint).
    /// Null: the one box over the footprint, tilted the same way. <see cref="HalfWidth"/>, <see cref="HalfLength"/> and <see cref="Height"/> stay the footprint the run-over, the enter reach and the seat schematic read.
    /// </summary>
    public List<float[]>? Hull { get; set; }

    HullBox[]? _hullBoxes;
    /// <summary>The boxes of the hull (<see cref="Hull"/>), or the one upright box of a type without one.</summary>
    public HullBox[] HullBoxes => _hullBoxes ??= Hull is { } h ? h.Select(b => new HullBox(b[0], b[1], b[2], b[3], b[4], b[5])).ToArray() : new[] { HullBox.Upright(HalfWidth, HalfLength, Height) };

    /// <summary>Whether the hull's boxes tilt with the pitch and roll: a type with an attitude (an aircraft, a trailer, a driven ground vehicle: every type in vehicles.json); one box or a <see cref="Hull"/> list alike (#483).</summary>
    public bool Tilts => Flight != null || Trailer != null || Drive != null;

    /// <summary>
    /// How far from the origin the hull reaches, metres (a cheap distance for a broad test): the footprint's half length and width added for the one upright box; for a <see cref="Hull"/>, the farthest corner of any
    /// box, whatever the attitude.
    /// </summary>
    public float BroadReach => _broadReach ??= Tilts ? HullBoxes.Max(b => MathF.Sqrt(MathF.Pow(MathF.Abs(b.Cx) + b.HalfWidth, 2) + MathF.Pow(MathF.Abs(b.Cz) + b.HalfLength, 2) + MathF.Pow(MathF.Max(MathF.Abs(b.Y0), MathF.Abs(b.Y1)), 2))) : HalfLength + HalfWidth;
    float? _broadReach;

    /// <summary>The pitch and roll the hull is turned by: the vehicle's attitude for a type that <see cref="Tilts"/>, else none (so a hull is looked at one way everywhere).</summary>
    public (float Pitch, float Roll) TiltOf(float pitch, float roll) => Tilts ? (pitch, roll) : (0f, 0f);
    /// <summary>Override of the file's enter_radius for this type (metres from the footprint). Null: the file's.</summary>
    public float? EnterRadius { get; set; }
    /// <summary>Seats in order; seat 0 is the driver. Default: one driver seat.</summary>
    public List<VehicleSeatDef> Seats { get; set; } = new() { new VehicleSeatDef { Role = "driver" } };
    /// <summary>The gun a gunner seat fires. Null: no gun.</summary>
    public VehicleGunDef? Gun { get; set; }
    /// <summary>
    /// The gunner sits in the open (the flak truck's turret): shots that reach his body hit him, not the hull around him (<see cref="GunnerCover"/>).
    /// False keeps every rider behind the hull, as the Humvee's covered seats are.
    /// </summary>
    public bool ExposedGunner { get; set; }
    /// <summary>
    /// Whether the hull leaves the rider of <paramref name="seat"/> uncovered: the seat says so itself (<see cref="VehicleSeatDef.Exposed"/>, the
    /// amphibious transport's three), or it is the gunner seat of a vehicle with <see cref="ExposedGunner"/> (the flak truck, the utility helicopter).
    /// </summary>
    public bool SeatExposed(int seat) => CoverOf(seat) == SeatCover.Exposed;
    /// <summary>
    /// The cover a seat declares: its own <see cref="VehicleSeatDef.Cover"/>, else Exposed for the gunner seat of a vehicle with <see cref="ExposedGunner"/>
    /// (the shorthand for that seat), else null (not declared).
    /// </summary>
    public SeatCover? DeclaredCover(int seat) => seat < 0 || seat >= Seats.Count ? null : Seats[seat].Cover ?? (ExposedGunner && Seats[seat].Role == "gunner" ? SeatCover.Exposed : null);
    /// <summary>The cover of a seat as the rules read it: the declared one, Glass for a seat that declares none.</summary>
    public SeatCover CoverOf(int seat) => DeclaredCover(seat) ?? SeatCover.Glass;
    /// <summary>An anti-aircraft turret worked from the gunner seat (the flak truck). Null: none. Excludes <see cref="Gun"/>.</summary>
    public VehicleFlakDef? Flak { get; set; }
    /// <summary>
    /// Where the <see cref="Gun"/> is mounted and how far it swings (the utility helicopter's door gun). Null: the gun fires along the gunner's
    /// view from above the seat (the roof gun of a covered seat). With it, the gun is on a pintle and the aim is held inside its arc (<see cref="DoorGunRules"/>).
    /// </summary>
    public VehicleDoorGunDef? DoorGun { get; set; }
    /// <summary>Mass (kg) for run-over damage. Unset: the v1 run-over numbers (see <see cref="VehiclesFile.RunOverScale"/>).</summary>
    public float? RunOverMass { get; set; }
    /// <summary>
    /// Where the type travels (<see cref="VehicleMedium"/>): "land" (default), "amphibious" (drives on land, floats on water at
    /// <see cref="WaterTopSpeed"/>) or "air" (it flies:
    /// the utility helicopter, stepped by <see cref="FlightModel"/> with the numbers of <see cref="Flight"/>).
    /// </summary>
    public string Medium { get; set; } = VehicleMedium.Land;
    /// <summary>Amphibious only: top speed afloat, m/s.</summary>
    public float WaterTopSpeed { get; set; } = 7;
    /// <summary>Amphibious only: forward acceleration afloat, m/s².</summary>
    public float WaterAcceleration { get; set; } = 1.5f;
    /// <summary>Whether a driver running someone over hurts them. Land types, and an amphibious only on land.</summary>
    public bool RunOver { get; set; } = true;
    /// <summary>Whether a wall impact hurts the vehicle.</summary>
    public bool CrashDamage { get; set; } = true;
    /// <summary>True for a type that can float (the amphibious transport): its origin rides the water surface when it is over a water body.</summary>
    public bool CanFloat => Medium == VehicleMedium.Amphibious;
    /// <summary>The flight handling of a type with medium "air" (vehicles.json "flight"); null for everything that drives or floats.</summary>
    public VehicleFlightDef? Flight { get; set; }
    /// <summary>
    /// The handling of a type its driver's client predicts (vehicles.json "drive", the amphibious transport, ADR 0010): it is stepped by
    /// <see cref="DriveModel"/> and never by <see cref="CarModel"/>. Null for every other type. A land type's block has no water numbers in use (see <see cref="VehicleDriveDef.Validate"/>).
    /// </summary>
    public VehicleDriveDef? Drive { get; set; }
    /// <summary>The bay that takes loads (vehicles.json "cargo_volume", the Cartel van's: <see cref="Vehicles.CargoVolume.Contains"/>). Null: none.</summary>
    public VehicleCargoVolumeDef? CargoVolume { get; set; }
    /// <summary>True (default): a destroyed vehicle of this type comes back after <see cref="VehiclesFile.DestroyedReturnTime"/>. False (the Scud truck): it is gone for the match, and a new round retires it instead of resetting it.</summary>
    public bool Returns { get; set; } = true;
    /// <summary>A missile launcher (vehicles.json "launcher", the Scud truck): which seat is the operator's and where the rocket leaves. Null: none. See <see cref="ScudLauncher"/>.</summary>
    public VehicleLauncherDef? Launcher { get; set; }
    /// <summary>The type can pull a trailer (vehicles.json "tow": the hitch and what the load does to it). Null: it cannot. See <see cref="TowModel"/>.</summary>
    public VehicleTowDef? Tow { get; set; }
    /// <summary>The type is a single-axle trailer (vehicles.json "trailer": drawbar eye, axle, mass) with no seats, pulled by a type with a <see cref="Tow"/> block. Null: it is not.</summary>
    public VehicleTrailerDef? Trailer { get; set; }
    /// <summary>A single fixed body colour [r, g, b] (sRGB) for a type with one look (olive drab). Replaces the colour variants: such a type has one variant.</summary>
    public float[]? Tint { get; set; }
    /// <summary>Model file relative to assets/, or a private-pack model id (empty: no model, a box). Scale, yaw and offset fit the raw node to the game (forward -Z, origin at the ground centre).</summary>
    public string Model { get; set; } = "";
    public float ModelScale { get; set; } = 1;
    public float ModelYawDeg { get; set; }
    /// <summary>Headlights and tail lights (vehicles.json "lights"); null for a type without lamps.</summary>
    public VehicleLights? Lights { get; set; }
    /// <summary>The engine sound set of this type (vehicles.json "sounds"); null keeps the shared vehicle.engine loop.</summary>
    public VehicleSoundsDef? Sounds { get; set; }
    /// <summary>The type sounds a backup alarm while its driver reverses (vehicles.json "reverse_beep", the bigger vehicles, #432). See <see cref="ReverseBeep"/>.</summary>
    public bool ReverseBeep { get; set; }
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
    /// <summary>
    /// Colour variants of a Synty model (#325): one standardized model per body colour (`pack:` ids, the first is <see cref="Model"/>), each wearing another atlas. An atlas swap, never a tint.
    /// The colour index of the vehicle record picks the skin (<see cref="SkinModel"/>), so every client draws the same one without a new wire field.
    /// </summary>
    public List<string> Skins { get; set; } = new();
    /// <summary>The model to draw for a colour index: its skin, or <see cref="Model"/> when the type has no skins.</summary>
    public string SkinModel(int colour) => Skins.Count > 0 ? Skins[colour % Skins.Count] : Model;
    /// <summary>Number of colour variants: the skins, the model's paints, or the shared car palette.</summary>
    public int Variants => Skins.Count > 0 ? Skins.Count : Tint != null ? 1 : Paints.Count > 0 ? Paints.Count : VehicleColours.Palette.Length;
}

/// <summary>How a vehicle's collision sounds (<see cref="VehiclesFile.CrashSoundOf"/>, #410): a bump that costs nothing, or a crash that hurts the hull.</summary>
public enum CrashSound : byte { None, Light, Hard }

/// <summary><see cref="EventKind.VehicleCrash"/>: u16 vehicle, u8 <see cref="CrashSound"/> (never None).</summary>
public static class VehicleCrashCodec
{
    public static byte[] Encode(ushort car, CrashSound sound) => new[] { (byte)car, (byte)(car >> 8), (byte)sound };

    public static (ushort Car, CrashSound Sound)? Decode(ReadOnlySpan<byte> p) =>
        p.Length == 3 && p[2] is (byte)CrashSound.Light or (byte)CrashSound.Hard ? ((ushort)(p[0] | p[1] << 8), (CrashSound)p[2]) : null;
}

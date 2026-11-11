namespace Suchohrad.ShooterCore;

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
    /// <summary>Weapon damage against vehicles is the weapon's damage times these (hitscan bullets, rockets and their splash).</summary>
    public float BulletDamageMultiplier { get; set; } = 0.5f;
    public float RocketDamageMultiplier { get; set; } = 1.5f;
    /// <summary>Mass the run-over numbers above are tuned for (the car). A type with <see cref="VehicleDef.RunOverMass"/> scales its run-over damage by its mass over this.</summary>
    public float RunOverReferenceMass { get; set; } = 1200;
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
            if (v.Hood.Length != 3) throw new DataFileException($"vehicles.json: {v.Id}: hood is [x, y, z]");
            if (v.Variants is < 1 or > 16) throw new DataFileException($"vehicles.json: {v.Id}: variants must be 1..16");
            if (v.Paints.Count > 0 && v.PaintMaterial == "") throw new DataFileException($"vehicles.json: {v.Id}: paints need paint_material");
            if (v.Lights is { } l)
            {
                if (l.Head.Count is < 1 or > 4 || l.Tail.Count > 4) throw new DataFileException($"vehicles.json: {v.Id}: lights need 1 to 4 head and 0 to 4 tail lamps");
                if (l.Head.Concat(l.Tail).Any(p => p is null || p.Length != 3)) throw new DataFileException($"vehicles.json: {v.Id}: each lamp is [x, y, z]");
                if (l.BeamRange <= 0 || l.BeamAngle <= 0 || l.BeamAngle >= 89) throw new DataFileException($"vehicles.json: {v.Id}: beam_range must be positive, beam_angle 1..88 degrees");
            }
            if (v.Tint is { Length: not 3 } || v.Tint != null && v.Paints.Count > 0) throw new DataFileException($"vehicles.json: {v.Id}: tint is [r, g, b] and excludes paints");
            if (v.Seats.Count is < 1 or > 4 || v.Seats[0].Role != "driver") throw new DataFileException($"vehicles.json: {v.Id}: seats are 1..4 and the first is the driver");
            foreach (var seat in v.Seats)
            {
                if (seat.Role is not ("driver" or "gunner" or "passenger")) throw new DataFileException($"vehicles.json: {v.Id}: seat role {seat.Role} is not driver, gunner or passenger");
                if (seat.Role == "driver" && v.Seats.Count(x => x.Role == "driver") != 1) throw new DataFileException($"vehicles.json: {v.Id}: one driver seat");
                if (seat.Offset.Length != 3) throw new DataFileException($"vehicles.json: {v.Id}: seat offset is [x, y, z]");
            }
            if (v.Gun is { } g)
            {
                if (g.Damage <= 0 || g.FireInterval <= 0 || g.Range <= 0 || g.Magazine < 1 || g.Reserve < 0 || g.ReloadTime < 0)
                    throw new DataFileException($"vehicles.json: {v.Id}: gun damage, fire_interval, range and magazine must be positive, reserve and reload_time not negative");
                if (!v.Seats.Any(x => x.Role == "gunner")) throw new DataFileException($"vehicles.json: {v.Id}: a gun needs a gunner seat");
            }
            if (v.Height <= 0 || v.HalfWidth <= 0 || v.HalfLength <= 0) throw new DataFileException($"vehicles.json: {v.Id}: height, half_width and half_length must be positive");
            if (v.RunOverMass is { } rm && rm <= 0) throw new DataFileException($"vehicles.json: {v.Id}: run_over_mass must be positive");
            if (!VehicleMedium.All.Contains(v.Medium)) throw new DataFileException($"vehicles.json: {v.Id}: medium {v.Medium} is not land, amphibious or boat");
            if (v.Medium != VehicleMedium.Land && (v.WaterTopSpeed <= 0 || v.WaterAcceleration <= 0 || v.LandDecel <= 0 || v.PlaningSpeed < 0))
                throw new DataFileException($"vehicles.json: {v.Id}: water_top_speed, water_acceleration and land_decel must be positive, planing_speed not negative");
            if (v.Medium == VehicleMedium.Boat && v.Gun != null) throw new DataFileException($"vehicles.json: {v.Id}: a boat carries no gun");
        }
        if (f.DamageStages.Count != 3 || f.DamageStages.Any(x => x <= 0 || x >= 1) || f.DamageStages[0] <= f.DamageStages[1] || f.DamageStages[1] <= f.DamageStages[2])
            throw new DataFileException("vehicles.json: damage_stages are three descending fractions between 0 and 1");
        if (f.Explosion.Splash.Radius <= 0) throw new DataFileException("vehicles.json: explosion.splash.radius must be positive");
        if (f.RunOverReferenceMass <= 0) throw new DataFileException("vehicles.json: run_over_reference_mass must be positive");
        return f;
    }

    /// <summary>Run-over damage multiplier of a type: its run_over_mass over the reference mass, or 1 when it has none (the v1 numbers).</summary>
    public float RunOverScale(VehicleDef v) => v.RunOverMass is { } m ? m / RunOverReferenceMass : 1f;
}

/// <summary>vehicles.json "medium" values (see <see cref="VehicleDef.Medium"/>).</summary>
public static class VehicleMedium
{
    public const string Land = "land", Amphibious = "amphibious", Boat = "boat";
    public static readonly string[] All = { Land, Amphibious, Boat };
}

/// <summary>One seat of a vehicle (vehicles.json "seats"). Seat 0 is always the driver. Offset is in the vehicle's frame, metres: x right, y up, z back (forward is -Z), origin on the ground at the car's centre.</summary>
public class VehicleSeatDef
{
    /// <summary>"driver" (seat 0 only, drives), "gunner" (fires the vehicle's gun) or "passenger".</summary>
    public string Role { get; set; } = "passenger";
    public float[] Offset { get; set; } = { 0, 0, 0 };
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
    /// <summary>Lateral acceleration the tyres (or the lean) hold, m/s². Past the speed where v²/this beats TurnRadius, the turn widens with speed.</summary>
    public float MaxLateralAccel { get; set; } = 8;
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
    /// <summary>
    /// Height of the hit volume, metres from the ground: the top of the visible body (roof, cabin, rider's head). A bullet that passes over
    /// it misses; one that enters the footprint below it hits. See <see cref="VehicleBox"/>.
    /// </summary>
    public float Height { get; set; } = 1.2f;
    /// <summary>Seats in order; seat 0 is the driver. Default: one driver seat.</summary>
    public List<VehicleSeatDef> Seats { get; set; } = new() { new VehicleSeatDef { Role = "driver" } };
    /// <summary>The gun a gunner seat fires. Null: no gun.</summary>
    public VehicleGunDef? Gun { get; set; }
    /// <summary>Mass (kg) for run-over damage. Unset: the v1 run-over numbers (see <see cref="VehiclesFile.RunOverScale"/>).</summary>
    public float? RunOverMass { get; set; }
    /// <summary>
    /// Where the type travels (<see cref="VehicleMedium"/>): "land" (default), "amphibious" (drives on land, floats on water at
    /// <see cref="WaterTopSpeed"/>) or "boat" (water only: on land it coasts to a stop at <see cref="LandDecel"/>).
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
    public bool CanFloat => Medium != VehicleMedium.Land;
    /// <summary>A single fixed body colour [r, g, b] (sRGB) for a type with one look (olive drab). Replaces the colour variants: such a type has one variant.</summary>
    public float[]? Tint { get; set; }
    /// <summary>Model file relative to assets/ (empty: the code-built car with the Kenney sedan). Scale, yaw and offset fit the raw node to the game (forward -Z, origin at the ground centre).</summary>
    public string Model { get; set; } = "";
    public float ModelScale { get; set; } = 1;
    public float ModelYawDeg { get; set; }
    /// <summary>Headlights and tail lights (vehicles.json "lights"); null for a type without lamps.</summary>
    public VehicleLights? Lights { get; set; }
    /// <summary>Offset applied to the model node before scale, as [x, y, z].</summary>
    public float[] ModelOffset { get; set; } = { 0, 0, 0 };
    /// <summary>The model's own material that takes the colour variants, and the model's own materials whose colours the variants are (index = colour byte).</summary>
    public string PaintMaterial { get; set; } = "";
    public List<string> Paints { get; set; } = new();
    /// <summary>Number of colour variants: the model's paints, or the shared car palette.</summary>
    public int Variants => Tint != null ? 1 : Paints.Count > 0 ? Paints.Count : VehicleColours.Palette.Length;
}

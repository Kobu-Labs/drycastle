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
            if (v.Hood.Length != 3) throw new DataFileException($"vehicles.json: {v.Id}: hood is [x, y, z]");
            if (v.Variants is < 1 or > 16) throw new DataFileException($"vehicles.json: {v.Id}: variants must be 1..16");
            if (v.Paints.Count > 0 && v.PaintMaterial == "") throw new DataFileException($"vehicles.json: {v.Id}: paints need paint_material");
        }
        if (f.DamageStages.Count != 3 || f.DamageStages.Any(x => x <= 0 || x >= 1) || f.DamageStages[0] <= f.DamageStages[1] || f.DamageStages[1] <= f.DamageStages[2])
            throw new DataFileException("vehicles.json: damage_stages are three descending fractions between 0 and 1");
        if (f.Explosion.Splash.Radius <= 0) throw new DataFileException("vehicles.json: explosion.splash.radius must be positive");
        return f;
    }
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

public class VehicleDef
{
    public string Id { get; set; } = "";
    public float TopSpeed { get; set; }
    public float Acceleration { get; set; }
    public float Steering { get; set; }
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
    /// <summary>Model file relative to assets/ (empty: the code-built car with the Kenney sedan). Scale, yaw and offset fit the raw node to the game (forward -Z, origin at the ground centre).</summary>
    public string Model { get; set; } = "";
    public float ModelScale { get; set; } = 1;
    public float ModelYawDeg { get; set; }
    /// <summary>Offset applied to the model node before scale, as [x, y, z].</summary>
    public float[] ModelOffset { get; set; } = { 0, 0, 0 };
    /// <summary>The model's own material that takes the colour variants, and the model's own materials whose colours the variants are (index = colour byte).</summary>
    public string PaintMaterial { get; set; } = "";
    public List<string> Paints { get; set; } = new();
    /// <summary>Number of colour variants: the model's paints, or the shared car palette.</summary>
    public int Variants => Paints.Count > 0 ? Paints.Count : VehicleColours.Palette.Length;
}

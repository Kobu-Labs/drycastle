namespace Suchohrad.ShooterCore;

public enum WeaponKind { Hitscan, Projectile, Melee }

/// <summary>weapons.json. Numbers from the weapon ticket (#76); all tunable in the file.</summary>
public class WeaponsFile : IVersioned
{
    public int Version { get; set; }
    /// <summary>Hotbar order: slot 1 is index 0. <see cref="StartingWeapon"/> is the id equipped at spawn.</summary>
    public string StartingWeapon { get; set; } = "";
    public List<WeaponDef> Weapons { get; set; } = new();

    public WeaponDef? Find(string id) => Weapons.FirstOrDefault(w => w.Id == id);

    public static WeaponsFile Load(string path)
    {
        return Validate(DataFile.Load<WeaponsFile>(path));
    }

    public static WeaponsFile Validate(WeaponsFile f)
    {
        if (f.Weapons.Count == 0) throw new DataFileException("weapons.json: no weapons");
        if (f.Weapons.Select(w => w.Id).Distinct().Count() != f.Weapons.Count)
            throw new DataFileException("weapons.json: duplicate weapon id");
        if (f.Find(f.StartingWeapon) is null)
            throw new DataFileException($"weapons.json: starting_weapon \"{f.StartingWeapon}\" is not a weapon");
        return f;
    }
}

public class WeaponDef
{
    public string Id { get; set; } = "";
    public WeaponKind Kind { get; set; }
    public bool FullAuto { get; set; }
    public float Damage { get; set; }
    /// <summary>Hitscan headshot multiplier; 1 means no headshot bonus.</summary>
    public float HeadshotMultiplier { get; set; } = 1;
    /// <summary>Melee backstab damage (0 = none).</summary>
    public float BackstabDamage { get; set; }
    public float FireInterval { get; set; }
    public int MagazineSize { get; set; }
    public int ReserveAmmo { get; set; }
    public float ReloadTime { get; set; }
    public float MoveSpeedMultiplier { get; set; } = 1;

    // Hitscan range model: full damage to FalloffStart m, linear to FalloffMultiplier at FalloffEnd m.
    public float FalloffStart { get; set; }
    public float FalloffEnd { get; set; }
    public float FalloffMultiplier { get; set; } = 1;

    // Spread in degrees: base, added per shot (capped at SpreadMax), seconds to recover.
    public float SpreadBase { get; set; }
    public float SpreadPerShot { get; set; }
    public float SpreadMax { get; set; }
    public float SpreadRecovery { get; set; }

    /// <summary>Hitscan range in metres; 0 uses the server default (300).</summary>
    public float Range { get; set; }
    /// <summary>Vertical FOV in degrees while scoped (hold zoom); 0 = no scope.</summary>
    public float ZoomFov { get; set; }

    // Melee
    public float Reach { get; set; }

    // Projectile
    public float ProjectileSpeed { get; set; }
    public float ProjectileLife { get; set; }
    public SplashDef? Splash { get; set; }
}

public class SplashDef
{
    public float Radius { get; set; }
    public float FullDamageRadius { get; set; }
    public float EdgeDamage { get; set; }
    public float SelfDamageMultiplier { get; set; } = 1;
    /// <summary>
    /// Knockback speed (m/s) added to a player hit at full splash strength; scales with the splash fraction (damage / full damage).
    /// Tuned small (4.5 m/s: a point-blank blast lifts about 0.5 m, a rocket jump is a hop, not flight); ground friction
    /// kills the horizontal part within a few ticks. 0 disables knockback.
    /// </summary>
    public float KnockbackSpeed { get; set; } = 4.5f;
}

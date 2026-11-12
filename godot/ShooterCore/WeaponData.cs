namespace Drycastle.ShooterCore;

public enum WeaponKind { Hitscan, Projectile, Melee }

/// <summary>Weapon category (weapons.json "category"). Melee and Launcher map to their own kinds; the rest are hitscan guns.</summary>
public enum WeaponCategory { MachineGun, AssaultRifle, Shotgun, Smg, Sniper, Pistol, Launcher, Melee }

/// <summary>Loadout slot (weapons.json "slot"). Special is carried outside the primary/secondary choice (knife, bazooka).</summary>
public enum WeaponSlot { Primary, Secondary, Special }

/// <summary>
/// weapons.json, schema version 2. Loaded in two steps: the raw file (nullable fields, so "absent" is distinct from 0) is
/// merged with <c>category_defaults</c> (weapon value wins, then category value, then the built-in default) and validated
/// into the flat <see cref="WeaponDef"/> list the simulation reads. Hotbar order is the file order.
/// </summary>
public class WeaponsFile
{
    /// <summary>Newest weapons.json version this code reads. Bumped independently of match.json and vehicles.json.</summary>
    public const int SupportedVersion = 2;

    public int Version { get; init; }
    /// <summary>Hotbar order: slot 1 is index 0. <see cref="StartingWeapon"/> is the id equipped at spawn.</summary>
    public string StartingWeapon { get; init; } = "";
    public List<WeaponDef> Weapons { get; init; } = new();

    Dictionary<string, WeaponDef>? _byId;
    public WeaponDef? Find(string id) => DataIndex.Find(ref _byId, Weapons, w => w.Id, id);

    public static WeaponsFile Load(string path) => Resolve(DataFile.Load<WeaponsRaw>(path, SupportedVersion));

    public static WeaponsFile Parse(string json) => Resolve(DataFile.Parse<WeaponsRaw>(json, "weapons.json", SupportedVersion));

    // ---- resolution and validation -------------------------------------------------------------------------------

    static WeaponsFile Resolve(WeaponsRaw raw)
    {
        if (raw.Version < SupportedVersion)
            throw new DataFileException($"weapons.json: version {raw.Version} is older than supported version {SupportedVersion}; "
                + "migrate the file (every weapon needs \"category\" and \"slot\")");
        if (raw.Weapons is null || raw.Weapons.Count == 0) throw new DataFileException("weapons.json: no weapons");

        var categoryDefaults = new Dictionary<WeaponCategory, TuningRaw>();
        foreach (var (key, block) in raw.CategoryDefaults ?? new())
        {
            var where = $"category_defaults \"{key}\"";
            if (!TryEnum(key, out WeaponCategory cat)) throw new DataFileException($"weapons.json: {where}: unknown category");
            CheckTuning(block, where);
            categoryDefaults[cat] = block;
        }

        var weapons = new List<WeaponDef>();
        var ids = new HashSet<string>();
        foreach (var r in raw.Weapons)
        {
            if (string.IsNullOrEmpty(r.Id)) throw new DataFileException($"weapons.json: weapon #{weapons.Count + 1}: field \"id\" is required");
            var where = $"weapon \"{r.Id}\"";
            if (!ids.Add(r.Id)) throw new DataFileException($"weapons.json: duplicate weapon id \"{r.Id}\"");
            weapons.Add(ResolveWeapon(r, where, categoryDefaults));
        }

        var file = new WeaponsFile { Version = raw.Version, StartingWeapon = raw.StartingWeapon ?? "", Weapons = weapons };
        if (file.Find(file.StartingWeapon) is not { } start)
            throw new DataFileException($"weapons.json: starting_weapon \"{file.StartingWeapon}\" is not a weapon");
        if (start.UnlockLevel != 0)
            throw new DataFileException($"weapons.json: starting_weapon \"{start.Id}\" must have unlock_level 0");
        return file;
    }

    static WeaponDef ResolveWeapon(WeaponRaw r, string where, Dictionary<WeaponCategory, TuningRaw> categoryDefaults)
    {
        if (!TryEnum(r.Kind, out WeaponKind kind)) throw Invalid(where, "kind", "must be hitscan, projectile or melee");
        if (!TryEnum(r.Category, out WeaponCategory category))
            throw Invalid(where, "category", "is required and must be one of the category names in the schema");
        if (!TryEnum(r.Slot, out WeaponSlot slot)) throw Invalid(where, "slot", "is required and must be primary, secondary or special");

        var expected = category switch
        {
            WeaponCategory.Melee => WeaponKind.Melee,
            WeaponCategory.Launcher => WeaponKind.Projectile,
            _ => WeaponKind.Hitscan,
        };
        if (kind != expected) throw Invalid(where, "kind", $"must be {expected.ToString().ToLowerInvariant()} for category {Snake(category)}");

        CheckTuning(r, where);
        NonNegative(r.Damage, where, "damage");
        NonNegative(r.BackstabDamage, where, "backstab_damage");
        NonNegative(r.HeadshotMultiplier, where, "headshot_multiplier");
        NonNegative(r.FireInterval, where, "fire_interval");
        NonNegative(r.MagazineSize, where, "magazine_size");
        NonNegative(r.ReserveAmmo, where, "reserve_ammo");
        NonNegative(r.ReloadTime, where, "reload_time");
        NonNegative(r.FalloffStart, where, "falloff_start");
        NonNegative(r.FalloffEnd, where, "falloff_end");
        NonNegative(r.FalloffMultiplier, where, "falloff_multiplier");
        NonNegative(r.Range, where, "range");
        NonNegative(r.ZoomFov, where, "zoom_fov");
        NonNegative(r.ScopeFov, where, "scope_fov");
        NonNegative(r.Reach, where, "reach");
        NonNegative(r.ProjectileSpeed, where, "projectile_speed");
        NonNegative(r.ProjectileLife, where, "projectile_life");
        NonNegative(r.UnlockLevel, where, "unlock_level");
        NonNegative(r.PelletSpread, where, "pellet_spread");
        NonNegative(r.DamagePerPellet, where, "damage_per_pellet");
        NonNegative(r.SwayAmount, where, "sway_amount");
        NonNegative(r.BreathHoldSeconds, where, "breath_hold_seconds");
        NonNegative(r.BreathRecoverSeconds, where, "breath_recover_seconds");
        NonNegative(r.QuickscopePenalty, where, "quickscope_penalty");
        if (r.Pellets is { } pellets && pellets < 1) throw Invalid(where, "pellets", "must be at least 1");
        if (r.PumpAction == true && r.FullAuto == true) throw Invalid(where, "pump_action", "cannot be combined with full_auto");
        if (r.ShellReload == true && (r.MagazineSize is not > 0 || r.ReloadTime is not > 0))
            throw Invalid(where, "shell_reload", "needs magazine_size and reload_time > 0 (reload_time is then per shell)");
        if (r.ZoomLevels is { } levels && levels < 1) throw Invalid(where, "zoom_levels", "must be at least 1");
        if (category == WeaponCategory.Shotgun && r.Pellets is null) throw Invalid(where, "pellets", "is required for a shotgun");

        var c = categoryDefaults.GetValueOrDefault(category) ?? new TuningRaw();
        var move = Pick(r.MoveSpeedMultiplier, c.MoveSpeedMultiplier, 1);
        var damage = r.Damage ?? 0;

        return new WeaponDef
        {
            Id = r.Id!,
            Kind = kind,
            Category = category,
            Slot = slot,
            UnlockLevel = r.UnlockLevel ?? 0,
            FullAuto = r.FullAuto ?? false,
            PumpAction = r.PumpAction ?? false,
            ShellReload = r.ShellReload ?? false,
            Damage = damage,
            HeadshotMultiplier = r.HeadshotMultiplier ?? 1,
            BackstabDamage = r.BackstabDamage ?? 0,
            FireInterval = r.FireInterval ?? 0,
            MagazineSize = r.MagazineSize ?? 0,
            ReserveAmmo = r.ReserveAmmo ?? 0,
            ReloadTime = r.ReloadTime ?? 0,
            MoveSpeedMultiplier = move,
            AdsMoveSpeedMultiplier = Pick(r.AdsMoveSpeedMultiplier, c.AdsMoveSpeedMultiplier, move),
            FalloffStart = r.FalloffStart ?? 0,
            FalloffEnd = r.FalloffEnd ?? 0,
            FalloffMultiplier = r.FalloffMultiplier ?? 1,
            SpreadBase = Pick(r.SpreadBase, c.SpreadBase, 0),
            SpreadPerShot = Pick(r.SpreadPerShot, c.SpreadPerShot, 0),
            SpreadMax = Pick(r.SpreadMax, c.SpreadMax, 0),
            SpreadRecovery = Pick(r.SpreadRecovery, c.SpreadRecovery, 0),
            ProneSpreadMultiplier = Pick(r.ProneSpreadMultiplier, c.ProneSpreadMultiplier, 1),
            CrouchSpreadMultiplier = Pick(r.CrouchSpreadMultiplier, c.CrouchSpreadMultiplier, 1),
            AdsSpreadMultiplier = Pick(r.AdsSpreadMultiplier, c.AdsSpreadMultiplier, 1),
            MoveSpread = Pick(r.MoveSpread, c.MoveSpread, 0),
            Recoil = MergeRecoil(r.Recoil, c.Recoil),
            Range = r.Range ?? 0,
            // scope_fov is the v2 name; zoom_fov (v1) is still read so existing files keep working.
            ZoomFov = r.ScopeFov ?? r.ZoomFov ?? 0,
            ZoomLevels = r.ZoomLevels ?? 1,
            SwayAmount = r.SwayAmount ?? 0,
            BreathHoldSeconds = r.BreathHoldSeconds ?? 0,
            BreathRecoverSeconds = r.BreathRecoverSeconds ?? 0,
            QuickscopePenalty = r.QuickscopePenalty ?? 0,
            Pellets = r.Pellets ?? 1,
            PelletSpread = r.PelletSpread ?? 0,
            DamagePerPellet = r.DamagePerPellet ?? damage,
            Reach = r.Reach ?? 0,
            ProjectileSpeed = r.ProjectileSpeed ?? 0,
            ProjectileLife = r.ProjectileLife ?? 0,
            Splash = r.Splash,
        };
    }

    static RecoilDef MergeRecoil(RecoilRaw? own, RecoilRaw? category) => new()
    {
        VerticalPerShot = Pick(own?.VerticalPerShot, category?.VerticalPerShot, 0),
        HorizontalSway = Pick(own?.HorizontalSway, category?.HorizontalSway, 0),
        RecoveryRate = Pick(own?.RecoveryRate, category?.RecoveryRate, 0),
        MaxClimb = Pick(own?.MaxClimb, category?.MaxClimb, 0),
        AdsMultiplier = Pick(own?.AdsMultiplier, category?.AdsMultiplier, 1),
        CrouchMultiplier = Pick(own?.CrouchMultiplier, category?.CrouchMultiplier, 1),
        ProneMultiplier = Pick(own?.ProneMultiplier, category?.ProneMultiplier, 1),
        Pattern = own?.Pattern ?? category?.Pattern ?? new(),
    };

    /// <summary>Checks the fields a category can default (a weapon or a category_defaults block).</summary>
    static void CheckTuning(TuningRaw t, string where)
    {
        Positive(t.MoveSpeedMultiplier, where, "move_speed_multiplier");
        Positive(t.AdsMoveSpeedMultiplier, where, "ads_move_speed_multiplier");
        NonNegative(t.SpreadBase, where, "spread_base");
        NonNegative(t.SpreadPerShot, where, "spread_per_shot");
        NonNegative(t.SpreadMax, where, "spread_max");
        NonNegative(t.SpreadRecovery, where, "spread_recovery");
        NonNegative(t.ProneSpreadMultiplier, where, "prone_spread_multiplier");
        NonNegative(t.CrouchSpreadMultiplier, where, "crouch_spread_multiplier");
        NonNegative(t.AdsSpreadMultiplier, where, "ads_spread_multiplier");
        NonNegative(t.MoveSpread, where, "move_spread");
        if (t.Recoil is not { } rc) return;
        NonNegative(rc.VerticalPerShot, where, "recoil.vertical_per_shot");
        NonNegative(rc.HorizontalSway, where, "recoil.horizontal_sway");
        NonNegative(rc.RecoveryRate, where, "recoil.recovery_rate");
        NonNegative(rc.MaxClimb, where, "recoil.max_climb");
        NonNegative(rc.AdsMultiplier, where, "recoil.ads_multiplier");
        NonNegative(rc.CrouchMultiplier, where, "recoil.crouch_multiplier");
        NonNegative(rc.ProneMultiplier, where, "recoil.prone_multiplier");
        if (rc.Pattern?.Any(v => !float.IsFinite(v)) == true) throw Invalid(where, "recoil.pattern", "must contain only finite numbers");
    }

    static void NonNegative(float? v, string where, string field)
    {
        if (v is < 0) throw Invalid(where, field, "must be >= 0");
    }

    static void NonNegative(int? v, string where, string field)
    {
        if (v is < 0) throw Invalid(where, field, "must be >= 0");
    }

    static void Positive(float? v, string where, string field)
    {
        if (v is <= 0) throw Invalid(where, field, "must be > 0");
    }

    static float Pick(float? own, float? category, float fallback) => own ?? category ?? fallback;

    static DataFileException Invalid(string where, string field, string rule) =>
        new($"weapons.json: {where}: field \"{field}\" {rule}");

    static bool TryEnum<T>(string? s, out T value) where T : struct, Enum
    {
        value = default;
        // snake_case file names ("machine_gun") match the enum names ignoring case and underscores; digits and signs never parse.
        if (string.IsNullOrEmpty(s) || !s.All(c => char.IsLetter(c) || c == '_')) return false;
        return Enum.TryParse(s.Replace("_", ""), ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    static string Snake(WeaponCategory c) => c switch
    {
        WeaponCategory.MachineGun => "machine_gun",
        WeaponCategory.AssaultRifle => "assault_rifle",
        _ => c.ToString().ToLowerInvariant(),
    };

    // ---- raw file shape (JSON) -----------------------------------------------------------------------------------

    internal sealed class WeaponsRaw : IVersioned
    {
        public int Version { get; set; }
        public string? StartingWeapon { get; set; }
        public Dictionary<string, TuningRaw>? CategoryDefaults { get; set; }
        public List<WeaponRaw>? Weapons { get; set; }
    }

    /// <summary>The fields a category block may default. Per-weapon values override them.</summary>
    internal class TuningRaw
    {
        public float? MoveSpeedMultiplier { get; set; }
        /// <summary>Move speed multiplier while aiming down sights; defaults to the move speed.</summary>
        public float? AdsMoveSpeedMultiplier { get; set; }
        public float? SpreadBase { get; set; }
        public float? SpreadPerShot { get; set; }
        public float? SpreadMax { get; set; }
        public float? SpreadRecovery { get; set; }
        public float? ProneSpreadMultiplier { get; set; }
        public float? CrouchSpreadMultiplier { get; set; }
        public float? AdsSpreadMultiplier { get; set; }
        /// <summary>Degrees of spread added at sprint speed or in the air; see <see cref="WeaponDef.MoveSpread"/>.</summary>
        public float? MoveSpread { get; set; }
        public RecoilRaw? Recoil { get; set; }
    }

    internal sealed class WeaponRaw : TuningRaw
    {
        public string? Id { get; set; }
        public string? Kind { get; set; }
        public string? Category { get; set; }
        public string? Slot { get; set; }
        public int? UnlockLevel { get; set; }
        public bool? FullAuto { get; set; }
        public bool? PumpAction { get; set; }
        public bool? ShellReload { get; set; }
        public float? Damage { get; set; }
        public float? HeadshotMultiplier { get; set; }
        public float? BackstabDamage { get; set; }
        public float? FireInterval { get; set; }
        public int? MagazineSize { get; set; }
        public int? ReserveAmmo { get; set; }
        public float? ReloadTime { get; set; }
        public float? FalloffStart { get; set; }
        public float? FalloffEnd { get; set; }
        public float? FalloffMultiplier { get; set; }
        public float? Range { get; set; }
        /// <summary>v1 name for the scoped FOV; superseded by <see cref="ScopeFov"/> when both are present.</summary>
        public float? ZoomFov { get; set; }
        public float? ScopeFov { get; set; }
        public int? ZoomLevels { get; set; }
        public float? SwayAmount { get; set; }
        public float? BreathHoldSeconds { get; set; }
        public float? BreathRecoverSeconds { get; set; }
        public float? QuickscopePenalty { get; set; }
        public int? Pellets { get; set; }
        public float? PelletSpread { get; set; }
        public float? DamagePerPellet { get; set; }
        public float? Reach { get; set; }
        public float? ProjectileSpeed { get; set; }
        public float? ProjectileLife { get; set; }
        public SplashDef? Splash { get; set; }
    }

    internal sealed class RecoilRaw
    {
        public float? VerticalPerShot { get; set; }
        public float? HorizontalSway { get; set; }
        public float? RecoveryRate { get; set; }
        public float? MaxClimb { get; set; }
        public float? AdsMultiplier { get; set; }
        public float? CrouchMultiplier { get; set; }
        public float? ProneMultiplier { get; set; }
        public List<float>? Pattern { get; set; }
    }
}

public class WeaponDef
{
    public string Id { get; set; } = "";
    public WeaponKind Kind { get; set; }
    public WeaponCategory Category { get; set; }
    public WeaponSlot Slot { get; set; }
    /// <summary>XP level that unlocks the weapon; 0 = available from the start.</summary>
    public int UnlockLevel { get; set; }
    public bool FullAuto { get; set; }
    /// <summary>Pump action: the fire interval is the pump cycle, and the weapon is never full auto (validated). No other runtime effect yet.</summary>
    public bool PumpAction { get; set; }
    /// <summary>Shell-by-shell reload: reload_time is the time per shell. Firing with a shell loaded interrupts the reload.</summary>
    public bool ShellReload { get; set; }
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
    /// <summary>Move speed multiplier while aiming down sights (resolved: the weapon's or category's value, else the move speed).</summary>
    public float AdsMoveSpeedMultiplier { get; set; } = 1;

    // Hitscan range model: full damage to FalloffStart m, linear to FalloffMultiplier at FalloffEnd m.
    public float FalloffStart { get; set; }
    public float FalloffEnd { get; set; }
    public float FalloffMultiplier { get; set; } = 1;

    // Spread in degrees: base, added per shot (capped at SpreadMax), seconds to recover.
    public float SpreadBase { get; set; }
    public float SpreadPerShot { get; set; }
    public float SpreadMax { get; set; }
    public float SpreadRecovery { get; set; }
    /// <summary>Spread multiplier while prone (1 = none; below 1 steadies the aim, snipers lowest).</summary>
    public float ProneSpreadMultiplier { get; set; } = 1;
    /// <summary>Spread multiplier while crouched (1 = none).</summary>
    public float CrouchSpreadMultiplier { get; set; } = 1;
    /// <summary>Spread multiplier while the aim button is held (1 = none).</summary>
    public float AdsSpreadMultiplier { get; set; } = 1;
    /// <summary>Degrees of spread added while moving: the full value at sprint speed or in the air, scaled by speed below that.</summary>
    public float MoveSpread { get; set; }

    /// <summary>Data-only recoil model (v2). Zero for weapons that do not set it; the runtime that applies it comes with the category work.</summary>
    public RecoilDef Recoil { get; set; } = new();

    /// <summary>Hitscan range in metres; 0 uses the server default (300).</summary>
    public float Range { get; set; }
    /// <summary>Vertical FOV in degrees while scoped (hold zoom); 0 = no scope. From scope_fov, else zoom_fov.</summary>
    public float ZoomFov { get; set; }
    /// <summary>Number of scope zoom steps (sniper). 1 = a single zoom level.</summary>
    public int ZoomLevels { get; set; } = 1;
    /// <summary>Sniper scope sway amount (0 = none).</summary>
    public float SwayAmount { get; set; }
    /// <summary>Seconds the sniper can hold its breath to steady the aim (0 = no breath hold).</summary>
    public float BreathHoldSeconds { get; set; }
    /// <summary>Seconds to recover after a breath hold ends.</summary>
    public float BreathRecoverSeconds { get; set; }
    /// <summary>Extra aim penalty for firing from the hip right after a scoped shot (sniper).</summary>
    public float QuickscopePenalty { get; set; }

    // Shotgun: pellets per shot, cone half-angle in degrees per pellet, damage per pellet (resolved: damage).
    public int Pellets { get; set; } = 1;
    public float PelletSpread { get; set; }
    public float DamagePerPellet { get; set; }

    // Melee
    public float Reach { get; set; }

    // Projectile
    public float ProjectileSpeed { get; set; }
    public float ProjectileLife { get; set; }
    public SplashDef? Splash { get; set; }
}

/// <summary>
/// Recoil model (runtime in <see cref="RecoilState"/>): vertical climb per shot, horizontal sway, optional per-shot horizontal
/// pattern (degrees, cycled by shot index; replaces the sway when set), recovery and a climb cap. Multipliers scale the kick
/// while aiming down sights or crouching (1 = no change). All values are degrees; 0 means "none" for the climb cap
/// (<see cref="MaxClimb"/>) and for recovery (<see cref="RecoveryRate"/>), so a recovery of 0 keeps the offset until the next burst.
/// </summary>
public class RecoilDef
{
    public float VerticalPerShot { get; set; }
    public float HorizontalSway { get; set; }
    public List<float> Pattern { get; set; } = new();
    /// <summary>Degrees per second the aim settles back down when the trigger is released.</summary>
    public float RecoveryRate { get; set; }
    /// <summary>Total climb (degrees) before sustained fire stops adding more. 0 = no cap.</summary>
    public float MaxClimb { get; set; }
    /// <summary>Kick multiplier while aiming down sights (zoom held).</summary>
    public float AdsMultiplier { get; set; } = 1;
    /// <summary>Kick multiplier while crouching.</summary>
    public float CrouchMultiplier { get; set; } = 1;
    /// <summary>Kick multiplier while prone (lying steadies the weapon; snipers lower).</summary>
    public float ProneMultiplier { get; set; } = 1;
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

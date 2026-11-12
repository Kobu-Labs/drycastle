namespace Drycastle.ShooterCore;

/// <summary>A flying kind: how it circles the map. Read from killstreaks.json "aircraft" (at most two kinds, see <see cref="AircraftState"/>).</summary>
public sealed class AircraftKindDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Radius of the circle it flies, metres.</summary>
    public float RadiusM { get; init; }
    /// <summary>Height above the ground under the circle centre, metres.</summary>
    public float AltitudeM { get; init; }
    /// <summary>Speed along the circle, metres per second.</summary>
    public float SpeedMps { get; init; }
    /// <summary>Largest bank angle the model shows, degrees (at most <see cref="AircraftState.MaxBankDeg"/>).</summary>
    public float MaxBankDeg { get; init; }
    /// <summary>Default lifetime; a streak may pass its own.</summary>
    public float LifetimeS { get; init; }
    /// <summary>Hit points: at 0 the aircraft is shot down (<see cref="WorldDespawnReason.Destroyed"/>). On the wire as a percentage of this.</summary>
    public float Hp { get; init; } = 600;
}

/// <summary>What a gunship weapon's shot is: a hitscan round or a rocket on the projectile sim.</summary>
public enum GunshipWeaponKind : byte
{
    /// <summary>A hitscan round from the turret (walls, players, dummies, vehicles), with an optional blast.</summary>
    Cannon,
    /// <summary>A rocket on the player rocket sim (its blast is the weapons.json entry's unless the weapon sets its own splash).</summary>
    Rocket,
    /// <summary>A shell: a hitscan trace to the first wall, player or vehicle, then a blast with its own <see cref="GunshipWeaponDef.Splash"/>.</summary>
    Shell,
}

/// <summary>
/// One weapon a gunship's turret fires. There is no ammo and no heat: the streak's time limit is the only limit, and the
/// fire interval is the pacing. <see cref="WeaponId"/> names a weapons.json entry: it gives the shot's sound and tracer (the
/// shot index on the wire), and for a rocket the flight and the default explosion (the rocket sim is the player's). A cannon uses its own
/// <see cref="Damage"/>, <see cref="RangeM"/>, <see cref="SpreadDeg"/> and optional <see cref="Splash"/>.
/// </summary>
public sealed class GunshipWeaponDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public GunshipWeaponKind Kind { get; init; }
    public string WeaponId { get; init; } = "";
    /// <summary>Damage of the round at its hit point (a cannon: its own; a shell: its direct hit).</summary>
    public float Damage { get; init; }
    public float RangeM { get; init; }
    public float IntervalS { get; init; }
    public float SpreadDeg { get; init; }
    /// <summary>
    /// The blast of a round (its own tuning, not weapons.json): a shell always has one, a cannon round may have one (its hit
    /// point takes the full damage, everything in the radius the falloff). A rocket may have one too: it then replaces the
    /// weapons.json blast for that launch (the gunship's rockets 8 m; the bazooka keeps its 6 m). Null uses the weapon's own.
    /// </summary>
    public SplashDef? Splash { get; init; }
    /// <summary>
    /// Visual only (#160): the tracer's speed and length. Cannon rounds and shells need them; a rocket has none (it flies in the
    /// rocket sim, with its own look). The damage is the server's, at the hit, whatever these are.
    /// </summary>
    public float? TracerSpeedMps { get; init; }
    public float? TracerLengthM { get; init; }
    /// <summary>
    /// A rocket's flight speed, m/s (killstreaks.json <c>projectile_speed_mps</c>, default 250). It replaces the bazooka's 40 m/s for this
    /// launch, so a rocket can reach a target across the map; its life is <see cref="RocketFlight.LifeFor"/> of its range and this speed.
    /// Zero for the other kinds.
    /// </summary>
    public float ProjectileSpeedMps { get; init; }
}

/// <summary>The gunship turret: aim limits relative to the airframe, zoom levels, and its weapons (killstreaks.json "gunship").</summary>
public sealed class GunshipDef
{
    /// <summary>Lowest and highest pitch, degrees (negative looks down).</summary>
    public float PitchMinDeg { get; init; }
    public float PitchMaxDeg { get; init; }
    /// <summary>Magnification steps, 1 and up, in cycle order. The zoom key steps through them.</summary>
    public IReadOnlyList<float> ZoomLevels { get; init; } = new[] { 1f };
    public IReadOnlyList<GunshipWeaponDef> Weapons { get; init; } = Array.Empty<GunshipWeaponDef>();
}

/// <summary>Validation and resolution of the aircraft and gunship sections of killstreaks.json.</summary>
public static class AircraftData
{
    /// <summary>Two kinds at most: the wire state carries the kind in one bit (<see cref="AircraftState"/>).</summary>
    public const int MaxKinds = 2;
    const string F = "killstreaks.json";

    internal sealed class AircraftRaw
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public float? RadiusM { get; set; }
        public float? AltitudeM { get; set; }
        public float? SpeedMps { get; set; }
        public float? MaxBankDeg { get; set; }
        public float? LifetimeS { get; set; }
        public float? Hp { get; set; }
    }

    internal sealed class GunshipRaw
    {
        public float? PitchMinDeg { get; set; }
        public float? PitchMaxDeg { get; set; }
        public List<float>? ZoomLevels { get; set; }
        public List<WeaponRaw>? Weapons { get; set; }
    }

    internal sealed class WeaponRaw
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public string? WeaponId { get; set; }
        public float? Damage { get; set; }
        public float? RangeM { get; set; }
        public float? IntervalS { get; set; }
        public float? SpreadDeg { get; set; }
        public SplashRaw? Splash { get; set; }
        public float? TracerSpeedMps { get; set; }
        public float? TracerLengthM { get; set; }
        public float? ProjectileSpeedMps { get; set; }
    }

    internal sealed class SplashRaw
    {
        public float? Radius { get; set; }
        public float? FullDamageRadius { get; set; }
        public float? EdgeDamage { get; set; }
        public float? SelfDamageMultiplier { get; set; }
        public float? KnockbackSpeed { get; set; }
    }

    static float Need(float? v, string where, string field, float min)
    {
        if (v is { } x && float.IsFinite(x) && x >= min) return x;
        throw new DataFileException($"{F}: {where}: field \"{field}\" is required and must be {min} or more");
    }

    /// <summary>The aircraft kinds, the gunship turret and the AC-130's turret (its own section, same shape).</summary>
    internal static (IReadOnlyList<AircraftKindDef> Kinds, GunshipDef? Gunship, GunshipDef? Ac130) Resolve(List<AircraftRaw>? aircraft, GunshipRaw? gunship, GunshipRaw? ac130)
    {
        var kinds = ResolveKinds(aircraft);
        return (kinds, gunship is null ? null : Build(gunship, "gunship"), ac130 is null ? null : Build(ac130, "ac130"));
    }

    static IReadOnlyList<AircraftKindDef> ResolveKinds(List<AircraftRaw>? aircraft)
    {
        var kinds = new List<AircraftKindDef>();
        var ids = new HashSet<string>();
        foreach (var r in aircraft ?? new List<AircraftRaw>())
        {
            if (string.IsNullOrEmpty(r.Id)) throw new DataFileException($"{F}: aircraft kind: field \"id\" is required");
            if (!ids.Add(r.Id)) throw new DataFileException($"{F}: duplicate aircraft kind \"{r.Id}\"");
            var where = $"aircraft \"{r.Id}\"";
            if (string.IsNullOrWhiteSpace(r.Name)) throw new DataFileException($"{F}: {where}: field \"name\" is required");
            var bank = Need(r.MaxBankDeg, where, "max_bank_deg", 0);
            if (bank > AircraftState.MaxBankDeg) throw new DataFileException($"{F}: {where}: field \"max_bank_deg\" must be {AircraftState.MaxBankDeg} or less");
            kinds.Add(new AircraftKindDef
            {
                Id = r.Id, Name = r.Name,
                RadiusM = Need(r.RadiusM, where, "radius_m", 1),
                AltitudeM = Need(r.AltitudeM, where, "altitude_m", 0),
                SpeedMps = Need(r.SpeedMps, where, "speed_mps", 0.1f),
                MaxBankDeg = bank,
                LifetimeS = Need(r.LifetimeS, where, "lifetime_s", 0.1f),
                Hp = Need(r.Hp, where, "hp", 1),
            });
        }
        if (kinds.Count > MaxKinds) throw new DataFileException($"{F}: at most {MaxKinds} aircraft kinds (the wire carries the kind in one bit)");

        return kinds;
    }

    /// <summary>One turret section ("gunship" or "ac130"): aim limits, zoom and weapons. Messages name the section.</summary>
    static GunshipDef Build(GunshipRaw gunship, string section)
    {
        var zoom = gunship.ZoomLevels is { Count: > 0 } z ? z.ToArray() : new[] { 1f };
        if (zoom.Any(v => !float.IsFinite(v) || v < 1f)) throw new DataFileException($"{F}: {section}: field \"zoom_levels\" must hold values of 1 or more");
        var pitchMin = gunship.PitchMinDeg ?? throw new DataFileException($"{F}: {section}: field \"pitch_min_deg\" is required");
        var pitchMax = gunship.PitchMaxDeg ?? throw new DataFileException($"{F}: {section}: field \"pitch_max_deg\" is required");
        if (pitchMin > pitchMax) throw new DataFileException($"{F}: {section}: \"pitch_min_deg\" must not be above \"pitch_max_deg\"");
        var weapons = new List<GunshipWeaponDef>();
        var wids = new HashSet<string>();
        foreach (var r in gunship.Weapons ?? new List<WeaponRaw>())
        {
            if (string.IsNullOrEmpty(r.Id)) throw new DataFileException($"{F}: {section} weapon: field \"id\" is required");
            if (!wids.Add(r.Id)) throw new DataFileException($"{F}: duplicate {section} weapon \"{r.Id}\"");
            var where = $"{section} weapon \"{r.Id}\"";
            if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.WeaponId))
                throw new DataFileException($"{F}: {where}: fields \"name\" and \"weapon_id\" are required");
            var kind = r.Kind switch
            {
                "cannon" => GunshipWeaponKind.Cannon,
                "rocket" => GunshipWeaponKind.Rocket,
                "shell" => GunshipWeaponKind.Shell,
                _ => throw new DataFileException($"{F}: {where}: field \"kind\" must be cannon, rocket or shell"),
            };
            SplashDef? splash = null;
            if (r.Splash is { } s)
            {
                var radius = Need(s.Radius, where, "splash.radius", 0.1f);
                var full = Need(s.FullDamageRadius, where, "splash.full_damage_radius", 0);
                if (full >= radius) throw new DataFileException($"{F}: {where}: \"splash.full_damage_radius\" must be less than \"splash.radius\"");
                splash = new SplashDef
                {
                    Radius = radius,
                    FullDamageRadius = full,
                    EdgeDamage = Need(s.EdgeDamage, where, "splash.edge_damage", 0),
                    SelfDamageMultiplier = Need(s.SelfDamageMultiplier, where, "splash.self_damage_multiplier", 0),
                    KnockbackSpeed = Need(s.KnockbackSpeed, where, "splash.knockback_speed", 0),
                };
            }
            else if (kind == GunshipWeaponKind.Shell) throw new DataFileException($"{F}: {where}: a shell needs a \"splash\" block");
            weapons.Add(new GunshipWeaponDef
            {
                Id = r.Id, Name = r.Name, Kind = kind, WeaponId = r.WeaponId,
                Damage = Need(r.Damage, where, "damage", 0),
                RangeM = Need(r.RangeM, where, "range_m", 1),
                IntervalS = Need(r.IntervalS, where, "interval_s", 0.01f),
                SpreadDeg = Need(r.SpreadDeg, where, "spread_deg", 0),
                Splash = splash,
                TracerSpeedMps = kind == GunshipWeaponKind.Rocket ? r.TracerSpeedMps : Need(r.TracerSpeedMps, where, "tracer_speed_mps", 50),
                TracerLengthM = kind == GunshipWeaponKind.Rocket ? r.TracerLengthM : Need(r.TracerLengthM, where, "tracer_length_m", 0.5f),
                ProjectileSpeedMps = kind == GunshipWeaponKind.Rocket ? Need(r.ProjectileSpeedMps ?? 250f, where, "projectile_speed_mps", 1) : 0,
            });
        }
        if (weapons.Count == 0) throw new DataFileException($"{F}: {section}: at least one weapon is required");
        return new GunshipDef
        {
            PitchMinDeg = pitchMin, PitchMaxDeg = pitchMax,
            ZoomLevels = zoom, Weapons = weapons,
        };
    }
}

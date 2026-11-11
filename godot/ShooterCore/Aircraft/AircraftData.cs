namespace Suchohrad.ShooterCore;

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
}

/// <summary>What a gunship weapon's shot is: a hitscan round or a rocket on the projectile sim.</summary>
public enum GunshipWeaponKind : byte { Cannon, Rocket }

/// <summary>
/// One weapon a gunship's turret fires, with its own ammo and heat. <see cref="WeaponId"/> names a weapons.json entry: it
/// gives the shot's sound and tracer (the shot index on the wire), and for a rocket the explosion numbers (the rocket sim
/// is the player's). A cannon uses its own <see cref="Damage"/>, <see cref="RangeM"/> and <see cref="SpreadDeg"/>.
/// </summary>
public sealed class GunshipWeaponDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public GunshipWeaponKind Kind { get; init; }
    public string WeaponId { get; init; } = "";
    public float Damage { get; init; }
    public float RangeM { get; init; }
    public float IntervalS { get; init; }
    public float SpreadDeg { get; init; }
    /// <summary>Rounds for the whole streak use (no reserve, no reload).</summary>
    public int Ammo { get; init; }
    /// <summary>Heat added per round, and the level at which the weapon locks.</summary>
    public float HeatPerShot { get; init; }
    public float HeatMax { get; init; }
    /// <summary>Heat lost per second. The lock lifts once heat falls to half of <see cref="HeatMax"/>.</summary>
    public float HeatCoolPerS { get; init; }
}

/// <summary>The gunship turret: aim limits relative to the airframe, zoom levels, and its weapons (killstreaks.json "gunship").</summary>
public sealed class GunshipDef
{
    /// <summary>Half-angle the turret can swing either side of the airframe's heading, degrees.</summary>
    public float YawLimitDeg { get; init; }
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
    }

    internal sealed class GunshipRaw
    {
        public float? YawLimitDeg { get; set; }
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
        public int? Ammo { get; set; }
        public float? HeatPerShot { get; set; }
        public float? HeatMax { get; set; }
        public float? HeatCoolPerS { get; set; }
    }

    static float Need(float? v, string where, string field, float min)
    {
        if (v is { } x && float.IsFinite(x) && x >= min) return x;
        throw new DataFileException($"{F}: {where}: field \"{field}\" is required and must be {min} or more");
    }

    internal static (IReadOnlyList<AircraftKindDef> Kinds, GunshipDef? Gunship) Resolve(List<AircraftRaw>? aircraft, GunshipRaw? gunship)
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
            });
        }
        if (kinds.Count > MaxKinds) throw new DataFileException($"{F}: at most {MaxKinds} aircraft kinds (the wire carries the kind in one bit)");

        if (gunship is null) return (kinds, null);
        var zoom = gunship.ZoomLevels is { Count: > 0 } z ? z.ToArray() : new[] { 1f };
        if (zoom.Any(v => !float.IsFinite(v) || v < 1f)) throw new DataFileException($"{F}: gunship: field \"zoom_levels\" must hold values of 1 or more");
        var pitchMin = gunship.PitchMinDeg ?? throw new DataFileException($"{F}: gunship: field \"pitch_min_deg\" is required");
        var pitchMax = gunship.PitchMaxDeg ?? throw new DataFileException($"{F}: gunship: field \"pitch_max_deg\" is required");
        if (pitchMin > pitchMax) throw new DataFileException($"{F}: gunship: \"pitch_min_deg\" must not be above \"pitch_max_deg\"");
        var weapons = new List<GunshipWeaponDef>();
        var wids = new HashSet<string>();
        foreach (var r in gunship.Weapons ?? new List<WeaponRaw>())
        {
            if (string.IsNullOrEmpty(r.Id)) throw new DataFileException($"{F}: gunship weapon: field \"id\" is required");
            if (!wids.Add(r.Id)) throw new DataFileException($"{F}: duplicate gunship weapon \"{r.Id}\"");
            var where = $"gunship weapon \"{r.Id}\"";
            if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.WeaponId))
                throw new DataFileException($"{F}: {where}: fields \"name\" and \"weapon_id\" are required");
            var kind = r.Kind switch
            {
                "cannon" => GunshipWeaponKind.Cannon,
                "rocket" => GunshipWeaponKind.Rocket,
                _ => throw new DataFileException($"{F}: {where}: field \"kind\" must be cannon or rocket"),
            };
            var ammo = r.Ammo ?? throw new DataFileException($"{F}: {where}: field \"ammo\" is required");
            if (ammo < 1) throw new DataFileException($"{F}: {where}: field \"ammo\" must be at least 1");
            var heatMax = Need(r.HeatMax, where, "heat_max", 1);
            var heatPerShot = Need(r.HeatPerShot, where, "heat_per_shot", 0);
            if (heatPerShot > heatMax) throw new DataFileException($"{F}: {where}: \"heat_per_shot\" must not exceed \"heat_max\"");
            weapons.Add(new GunshipWeaponDef
            {
                Id = r.Id, Name = r.Name, Kind = kind, WeaponId = r.WeaponId,
                Damage = Need(r.Damage, where, "damage", 0),
                RangeM = Need(r.RangeM, where, "range_m", 1),
                IntervalS = Need(r.IntervalS, where, "interval_s", 0.01f),
                SpreadDeg = Need(r.SpreadDeg, where, "spread_deg", 0),
                Ammo = ammo, HeatPerShot = heatPerShot, HeatMax = heatMax,
                HeatCoolPerS = Need(r.HeatCoolPerS, where, "heat_cool_per_s", 0),
            });
        }
        if (weapons.Count == 0) throw new DataFileException($"{F}: gunship: at least one weapon is required");
        var def = new GunshipDef
        {
            YawLimitDeg = Need(gunship.YawLimitDeg, "gunship", "yaw_limit_deg", 0),
            PitchMinDeg = pitchMin, PitchMaxDeg = pitchMax,
            ZoomLevels = zoom, Weapons = weapons,
        };
        return (kinds, def);
    }
}

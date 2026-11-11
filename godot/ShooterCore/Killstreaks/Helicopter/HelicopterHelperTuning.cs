using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>
/// Tunables of the helicopter helper (killstreaks.json, the "helper" block of "helicopter_helper"). The helper circles the
/// map and every <see cref="CycleMinS"/>..<see cref="CycleMaxS"/> seconds picks the best visible enemy, warns it for
/// <see cref="WarningS"/> seconds, then fires a short burst of <see cref="Rounds"/> rounds with its own accuracy.
/// </summary>
public sealed class HelicopterHelperTuning
{
    /// <summary>Pause between bursts: a random time in this range.</summary>
    public float CycleMinS { get; init; }
    public float CycleMaxS { get; init; }
    /// <summary>The red-dot warning the target gets before its burst.</summary>
    public float WarningS { get; init; }
    /// <summary>Rounds per burst, and the time between them.</summary>
    public int Rounds { get; init; }
    public float RoundIntervalS { get; init; }
    /// <summary>Damage of one round that hits.</summary>
    public float Damage { get; init; }
    /// <summary>Chance that a round hits a target it can see (0..1).</summary>
    public float Accuracy { get; init; }
    /// <summary>How far a missed round lands from the target's chest, metres.</summary>
    public float MissRadiusM { get; init; }
    /// <summary>Longest distance the helper engages at, metres.</summary>
    public float SightRangeM { get; init; }
    /// <summary>weapons.json id whose sound and tracer the rounds use.</summary>
    public string WeaponId { get; init; } = "";
    /// <summary>Exposure per stance: targets that are more exposed are preferred (score = distance / exposure).</summary>
    public float ExposureStand { get; init; }
    public float ExposureCrouch { get; init; }
    public float ExposureProne { get; init; }

    public float Exposure(Stance stance) => stance switch
    {
        Stance.Crouch => ExposureCrouch,
        Stance.Prone => ExposureProne,
        _ => ExposureStand,
    };

    /// <summary>The "helper" block of a killstreaks.json streak.</summary>
    internal sealed class HelperRaw
    {
        public float? CycleMinS { get; set; }
        public float? CycleMaxS { get; set; }
        public float? WarningS { get; set; }
        public int? Rounds { get; set; }
        public float? RoundIntervalS { get; set; }
        public float? Damage { get; set; }
        public float? Accuracy { get; set; }
        public float? MissRadiusM { get; set; }
        public float? SightRangeM { get; set; }
        public string? WeaponId { get; set; }
        public float? ExposureStand { get; set; }
        public float? ExposureCrouch { get; set; }
        public float? ExposureProne { get; set; }
    }

    static float Need(float? v, string where, string field, float min, float max = float.MaxValue)
    {
        if (v is { } x && float.IsFinite(x) && x >= min && x <= max) return x;
        throw new DataFileException($"killstreaks.json: {where}: field \"{field}\" is required and must be between {min} and {max}");
    }

    /// <summary>Validates the block; throws <see cref="DataFileException"/> naming the field.</summary>
    internal static HelicopterHelperTuning From(HelperRaw? r, string where)
    {
        if (r is null) throw new DataFileException($"killstreaks.json: {where}: a \"helper\" block is required for the helicopter helper");
        var min = Need(r.CycleMinS, where, "cycle_min_s", 0.1f);
        var max = Need(r.CycleMaxS, where, "cycle_max_s", min);
        if (max < min) throw new DataFileException($"killstreaks.json: {where}: \"cycle_max_s\" must not be below \"cycle_min_s\"");
        var rounds = r.Rounds ?? throw new DataFileException($"killstreaks.json: {where}: field \"rounds\" is required");
        if (rounds < 1) throw new DataFileException($"killstreaks.json: {where}: field \"rounds\" must be at least 1");
        if (string.IsNullOrWhiteSpace(r.WeaponId)) throw new DataFileException($"killstreaks.json: {where}: field \"weapon_id\" is required");
        return new HelicopterHelperTuning
        {
            CycleMinS = min, CycleMaxS = max,
            WarningS = Need(r.WarningS, where, "warning_s", 0),
            Rounds = rounds,
            RoundIntervalS = Need(r.RoundIntervalS, where, "round_interval_s", 0.01f),
            Damage = Need(r.Damage, where, "damage", 0),
            Accuracy = Need(r.Accuracy, where, "accuracy", 0, 1),
            MissRadiusM = Need(r.MissRadiusM, where, "miss_radius_m", 0),
            SightRangeM = Need(r.SightRangeM, where, "sight_range_m", 1),
            WeaponId = r.WeaponId,
            ExposureStand = Need(r.ExposureStand, where, "exposure_stand", 0.01f, 1),
            ExposureCrouch = Need(r.ExposureCrouch, where, "exposure_crouch", 0.01f, 1),
            ExposureProne = Need(r.ExposureProne, where, "exposure_prone", 0.01f, 1),
        };
    }
}

/// <summary>What the targeted player is told before a burst. Sent only to that player (EventKind.HelperWarning, 110).</summary>
/// <param name="Target">The targeted player's id.</param>
/// <param name="Helicopter">The helicopter's world entity id, so the client can keep the red dot on it.</param>
/// <param name="Seconds">How long the warning shows before the burst.</param>
public readonly record struct HelperWarning(ushort Target, ushort Helicopter, float Seconds);

public static class HelperWarningCodec
{
    /// <summary>Layout: u16 target, u16 helicopter, f32 seconds.</summary>
    public const int Size = 2 + 2 + 4;

    public static byte[] Encode(HelperWarning w)
    {
        var b = new byte[Size];
        BitConverter.TryWriteBytes(b.AsSpan(0, 2), w.Target);
        BitConverter.TryWriteBytes(b.AsSpan(2, 2), w.Helicopter);
        BitConverter.TryWriteBytes(b.AsSpan(4, 4), w.Seconds);
        return b;
    }

    /// <summary>Null on a short payload.</summary>
    public static HelperWarning? Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length < Size) return null;
        return new HelperWarning(BitConverter.ToUInt16(d.Slice(0, 2)), BitConverter.ToUInt16(d.Slice(2, 2)), BitConverter.ToSingle(d.Slice(4, 4)));
    }
}

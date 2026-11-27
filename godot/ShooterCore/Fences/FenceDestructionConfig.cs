using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Fences;

/// <summary>
/// fence_destruction.json (docs/fence-destruction.md Contract 5): the thin fences' piece hit points and round damage by kind, blasts, rams and the falling pieces.
/// Optional: a data directory without the file gets these defaults. The walls' strength is house_destruction.json's materials (brick, stone, concrete).
/// </summary>
public sealed record FenceDestructionConfig : IVersioned
{
    public int Version { get; init; } = 1;
    /// <summary>Off: no thin fence breaks (walls follow house_destruction.json).</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Keyed plain, picket, wire.</summary>
    public Dictionary<string, FenceKindDef> Kinds { get; init; } = DefaultKinds();
    public FenceBlastConfig Blast { get; init; } = new();
    public FenceRamConfig Ram { get; init; } = new();
    public FenceDebrisConfig Debris { get; init; } = new();

    FenceKindDef[] _byKind = [];

    /// <summary>The numbers of a thin kind (<see cref="FenceKind.Wall"/> throws).</summary>
    public FenceKindDef Of(FenceKind k) => k == FenceKind.Wall ? throw new ArgumentException("a wall has no pieces", nameof(k)) : _byKind[(int)k];

    public static string KindKey(FenceKind k) => k.ToString().ToLowerInvariant();

    public static FenceDestructionConfig Default => new FenceDestructionConfig().Validate();
    public static FenceDestructionConfig Parse(string json) => DataFile.Parse<FenceDestructionConfig>(json, "fence_destruction.json").Validate();
    public static FenceDestructionConfig Load(string path) => DataFile.Load<FenceDestructionConfig>(path).Validate();

    static Dictionary<string, FenceKindDef> DefaultKinds() => new()
    {
        ["plain"] = new() { PieceHp = 100, BulletDamage = 50 },
        ["picket"] = new() { PieceHp = 100, BulletDamage = 50 },
        ["wire"] = new() { PieceHp = 100, BulletDamage = 20 },
    };

    FenceDestructionConfig Validate()
    {
        const string f = "fence_destruction.json";
        var thin = new[] { FenceKind.Plain, FenceKind.Picket, FenceKind.Wire };
        foreach (var k in Kinds.Keys)
            if (!thin.Any(t => KindKey(t) == k)) throw new DataFileException($"{f}: kinds has no kind {k} (known: plain, picket, wire)");
        _byKind = new FenceKindDef[thin.Length];
        foreach (var t in thin)
        {
            if (!Kinds.TryGetValue(KindKey(t), out var d)) throw new DataFileException($"{f}: kinds.{KindKey(t)} is missing");
            if (d.PieceHp < 1 || d.BulletDamage < 0) throw new DataFileException($"{f}: kinds.{KindKey(t)}: piece_hp must be at least 1, bullet_damage not negative");
            _byKind[(int)t] = d;
        }
        if (!(Blast.RadiusShare >= 0) || Blast.Damage < 0) throw new DataFileException($"{f}: blast numbers must not be negative");
        if (!(Ram.MinSpeed > 0) || !(Ram.MomentumPerHp > 0) || !(Ram.ProbeM > 0)) throw new DataFileException($"{f}: ram numbers must be positive");
        if (Debris.MaxBodies < 0 || !(Debris.MaxDistanceM >= 0) || !(Debris.FadeAfterS >= 0) || !(Debris.FadeS >= 0) || Debris.StayCap < 0 || !(Debris.PushSpeed >= 0))
            throw new DataFileException($"{f}: debris numbers must not be negative");
        return this;
    }
}

/// <summary>fence_destruction.json <c>kinds.&lt;kind&gt;</c>.</summary>
public sealed record FenceKindDef
{
    /// <summary>Hit points of one piece.</summary>
    public int PieceHp { get; init; } = 100;
    /// <summary>What one round (bullet, pellet, vehicle gun round) takes off the piece it stops on.</summary>
    public int BulletDamage { get; init; } = 50;
}

/// <summary>fence_destruction.json <c>blast</c>: radius = splash radius * radius_share; damage at the centre, falling off linearly to the radius.</summary>
public sealed record FenceBlastConfig
{
    public float RadiusShare { get; init; } = 1.0f;
    public int Damage { get; init; } = 400;
}

/// <summary>fence_destruction.json <c>ram</c> (docs/fence-destruction.md F6).</summary>
public sealed record FenceRamConfig
{
    public bool Enabled { get; init; } = true;
    /// <summary>Speed a thin fence takes from the vehicle in one tick (m/s) from which it is a ram.</summary>
    public float MinSpeed { get; init; } = 2.0f;
    /// <summary>Momentum (kg m/s) that one hit point of a piece takes.</summary>
    public float MomentumPerHp { get; init; } = 2.0f;
    /// <summary>How far ahead of the stopped vehicle's box pieces are looked for (metres).</summary>
    public float ProbeM { get; init; } = 0.5f;
}

/// <summary>fence_destruction.json <c>debris</c>: the falling pieces, client only. The rubble mode is house destruction's.</summary>
public sealed record FenceDebrisConfig
{
    public int MaxBodies { get; init; } = 96;
    public float MaxDistanceM { get; init; } = 120;
    public float FadeAfterS { get; init; } = 20;
    public float FadeS { get; init; } = 2;
    /// <summary>Resting pieces that stay in "stay" mode at most; the oldest go first.</summary>
    public int StayCap { get; init; } = 3000;
    /// <summary>Speed of a piece's push when the message carries none (m/s).</summary>
    public float PushSpeed { get; init; } = 2.5f;
}

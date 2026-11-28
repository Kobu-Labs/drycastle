using System.Numerics;
using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Thieves;

/// <summary>
/// A Gunman's gun (GLOSSARY "Gunman"; rival_crew.json <c>gunman.gun</c>): how long it aims, how far off its rounds go, how far it shoots, where the muzzle is, and the weapons a Gunman may carry
/// (one per member, by its look number: <see cref="GangLook.Gun"/>). The damage, falloff, pellets and sound are the weapon's own in weapons.json.
/// </summary>
public sealed class GangGun
{
    /// <summary>The telegraph before each burst: a player who sees the Gunman stop and turn has this long to get into cover.</summary>
    public float AimSeconds { get; set; } = 0.8f;
    /// <summary>Half angle of the cone a round leaves in round the line to the target's chest, degrees (uniform by area: a pellet fan of one).</summary>
    public float AimErrorDeg { get; set; } = 3f;
    /// <summary>It shoots at a target no further than this (flat); further, or out of sight, it closes in.</summary>
    public float RangeM { get; set; } = 40;
    /// <summary>The muzzle: this high above its feet and this far ahead of them when it aims.</summary>
    public float MuzzleUpM { get; set; } = 1.4f;
    public float MuzzleAheadM { get; set; } = 0.45f;
    /// <summary>A Gunman closing in on a target it cannot see stops this near it.</summary>
    public float ChaseStopM { get; set; } = 2f;
    public List<GangGunWeapon> Weapons { get; set; } = new() { new() };

    public void Validate(string file, string where)
    {
        void Positive(float v, string what) { if (!(v > 0) || !float.IsFinite(v)) throw new DataFileException($"{file}: {where}.{what} must be positive"); }
        void NotNegative(float v, string what) { if (!(v >= 0) || !float.IsFinite(v)) throw new DataFileException($"{file}: {where}.{what} must not be negative"); }
        Positive(AimSeconds, "aim_seconds"); NotNegative(AimErrorDeg, "aim_error_deg"); Positive(RangeM, "range_m");
        Positive(MuzzleUpM, "muzzle_up_m"); NotNegative(MuzzleAheadM, "muzzle_ahead_m"); Positive(ChaseStopM, "chase_stop_m");
        if (AimErrorDeg >= 45) throw new DataFileException($"{file}: {where}.aim_error_deg is below 45");
        if (Weapons.Count == 0) throw new DataFileException($"{file}: {where}.weapons lists at least one weapon");
        foreach (var w in Weapons)
        {
            if (w.Weapon == "") throw new DataFileException($"{file}: {where}.weapons: every entry names a weapon of weapons.json");
            if (w.Burst < 1) throw new DataFileException($"{file}: {where}.weapons '{w.Weapon}': burst is at least 1");
            NotNegative(w.RoundGapSeconds, $"weapons '{w.Weapon}'.round_gap_seconds"); NotNegative(w.DamageScale, $"weapons '{w.Weapon}'.damage_scale");
        }
    }
}

/// <summary>One weapon a Gunman may carry: the weapon of weapons.json it fires (damage with falloff, pellets, the sound and casing clients play), its burst, what its rounds do to players, and the prop in its hand.</summary>
public sealed class GangGunWeapon
{
    public string Weapon { get; set; } = "deagle";
    /// <summary>Rounds per burst, and the time between two of them.</summary>
    public int Burst { get; set; } = 1;
    public float RoundGapSeconds { get; set; } = 0.35f;
    /// <summary>The weapon's damage (weapons.json, with its falloff and headshot multiplier) times this: a Gunman's rounds are weaker than a player's.</summary>
    public float DamageScale { get; set; } = 0.5f;
    /// <summary>Client only: the prop under assets-private/synty/glb and its atlas; missing, a dark box is drawn.</summary>
    public string Model { get; set; } = "";
    public string Atlas { get; set; } = "gang-warfare/textures/PolygonGangWarfare_01_A.png";
}

/// <summary>
/// One round a Gunman fires: who (its hit id), with what, from where, in which direction (the aim error already in it), how far it may fly, at whom it was aimed, and a seed for a shotgun's
/// pellet fan (spread round <see cref="Dir"/> by the weapon's pellet_spread).
/// </summary>
public readonly record struct GangShot(ushort MemberHitId, GangGunWeapon Weapon, Vector3 From, Vector3 Dir, float RangeM, ushort Target, uint Seed);

/// <summary>Where a posted member stands and which way it faces when it has nobody to fight (a trap house defender: <see cref="GangMember.Post"/>). <c>Seat</c>: the spot is a seat of a car (a Rival chase car's rider: sits, <see cref="GangRules.SeatedOf"/>). <c>Shut</c>: it sits where it cannot shoot out (the bay of an Assault van, no window): it never fights while posted there.</summary>
public readonly record struct GangPost(Vector3 At, float Facing, bool Seat = false, bool Shut = false);

/// <summary>How a crew notices a player it attacks on sight (<see cref="ThiefGang.HostileTo"/>): within <see cref="RadiusM"/> of a member that has a clear line from its eyes to the player's chest, looked at every <see cref="CheckSeconds"/>. A Gunman's own sight uses the same eyes, chest and clock.</summary>
public sealed class GangSight
{
    public float RadiusM { get; set; } = 35;
    public float CheckSeconds { get; set; } = 0.25f;
    public float EyeM { get; set; } = 1.6f;
    public float ChestM { get; set; } = 1.0f;

    public void Validate(string file, string where)
    {
        foreach (var (v, what) in new[] { (RadiusM, "radius_m"), (CheckSeconds, "check_seconds"), (EyeM, "eye_m"), (ChestM, "chest_m") })
            if (!(v > 0) || !float.IsFinite(v)) throw new DataFileException($"{file}: {where}.{what} must be positive");
    }
}

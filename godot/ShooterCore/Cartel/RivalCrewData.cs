using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// rival_crew.json, schema version 1: the numbers of the Rival crew (GLOSSARY "Rival crew", "Gunman"; docs/cartel.md "Assaults", "Trap house"), whose people are Gang members on the Thieves' brain
/// (<see cref="GangMember"/>). Its Enforcer is the Thieves' (thieves.json <c>gang.enforcer</c>) with a Hit count of its own; the Gunman is its own kind. A crew attacks the players it is told to on sight
/// (<see cref="Sight"/>, <see cref="ThiefGang.HostileTo"/>) and anyone who hits a member (<see cref="Aggro"/>). Killing a member pays no XP. Lengths metres, speeds metres a second, times seconds.
/// </summary>
public sealed class RivalCrewFile : IVersioned
{
    const string F = "rival_crew.json";

    public int Version { get; set; } = 1;
    public GangSight Sight { get; set; } = new();
    public GangAggro Aggro { get; set; } = new() { ForgetSeconds = 10, GiveUpM = 60 };
    public float CorpseSeconds { get; set; } = 10;
    public RivalEnforcer Enforcer { get; set; } = new();
    public GangKindConfig Gunman { get; set; } = new() { Hits = 6, WalkSpeed = 1.6f, RunSpeed = 4.8f, HitScale = 1f, Gun = new() };
    public RivalCrewView View { get; set; } = new();

    public static RivalCrewFile Parse(string json) => DataFile.Parse<RivalCrewFile>(json, F).Validate();

    public static RivalCrewFile Load(string path) => DataFile.Load<RivalCrewFile>(path).Validate();

    /// <summary>The defaults of every field, checked (a data directory without the file).</summary>
    public static RivalCrewFile Default => new RivalCrewFile().Validate();

    RivalCrewFile Validate()
    {
        void Positive(float v, string what) { if (!(v > 0) || !float.IsFinite(v)) throw new DataFileException($"{F}: {what} must be positive"); }
        Sight.Validate(F, "sight");
        Positive(Aggro.ForgetSeconds, "aggro.forget_seconds"); Positive(Aggro.GiveUpM, "aggro.give_up_m");
        if (Aggro.GiveUpM < Sight.RadiusM) throw new DataFileException($"{F}: aggro.give_up_m is not below sight.radius_m (a player seen would be let go at once)");
        Positive(CorpseSeconds, "corpse_seconds");
        Positive(Enforcer.Hits, "enforcer.hits");
        var g = Gunman;
        Positive(g.Hits, "gunman.hits"); Positive(g.WalkSpeed, "gunman.walk_speed"); Positive(g.RunSpeed, "gunman.run_speed"); Positive(g.HitScale, "gunman.hit_scale");
        if (g.Gun == null) throw new DataFileException($"{F}: gunman.gun is the Gunman's gun");
        g.Gun.Validate(F, "gunman.gun");
        if (View.Models.Count == 0) throw new DataFileException($"{F}: view.models lists at least one model");
        if (View.Grip.Position.Length != 3 || View.Grip.Rotation.Length != 4 || View.Grip.ModelRotationDeg.Length != 3 || View.Grip.ModelOffset.Length != 3)
            throw new DataFileException($"{F}: view.grip: position 3 numbers, rotation 4, model_rotation_deg 3, model_offset 3");
        if (View.Aim.Right is { Length: not 3 } || View.Aim.Left is { Length: not 3 }) throw new DataFileException($"{F}: view.aim: right and left are three numbers");
        return this;
    }

    /// <summary>Every weapon the Gunman may carry is a hitscan weapon of weapons.json (its damage, falloff, pellets and sound are used).</summary>
    public RivalCrewFile CheckAgainst(WeaponsFile weapons)
    {
        foreach (var w in Gunman.Gun!.Weapons)
        {
            var def = weapons.Weapons.FirstOrDefault(d => d.Id == w.Weapon) ?? throw new DataFileException($"{F}: gunman.gun.weapons '{w.Weapon}' is not in weapons.json");
            if (def.Kind != WeaponKind.Hitscan) throw new DataFileException($"{F}: gunman.gun.weapons '{w.Weapon}' is not a hitscan weapon");
        }
        return this;
    }

    /// <summary>
    /// The Gang member config of a Rival crew: the Thieves' (<paramref name="thieves"/>: walking, turning, routes, separation, hit weights, the Thief and the Gang boss) with the crew's Enforcer Hit count,
    /// its Gunman, sight, aggro and corpse time, and no XP for a kill.
    /// </summary>
    public GangConfig GangOf(GangConfig thieves)
    {
        var g = thieves.Copy();
        g.Enforcer = thieves.Enforcer.WithHits(Enforcer.Hits);
        g.Gunman = Gunman;
        g.Sight = Sight;
        g.Aggro = Aggro;
        g.CorpseSeconds = CorpseSeconds;
        g.KillXp = 0;
        return g;
    }
}

/// <summary>The Rival crew's Enforcer: the Thieves' (its speeds, pipe and size), with this Hit count.</summary>
public sealed class RivalEnforcer
{
    public float Hits { get; set; } = 8.75f;
}

/// <summary>How a Gunman is drawn (client only): its models (by look number, as the Thieves' kinds), its capsule colour without the pack, the gun's grip in the right hand, and the arm pose while it aims and fires.</summary>
public sealed class RivalCrewView
{
    public List<string> Models { get; set; } = new() { "gang_enforcer_01", "gang_thief_01" };
    public string GunmanColor { get; set; } = "#3b4656";
    public GangGrip Grip { get; set; } = new();
    /// <summary>The right hand forward at the shoulder while it aims and fires (the carry overlay's arm IK, <see cref="CarryPoseData"/>: rest-pose axes, its right hand is -X, forward +Z).</summary>
    public CarryPoseData Aim { get; set; } = new() { Right = new[] { -0.2f, 1.4f, 0.45f }, ElbowDown = 0.4f, ElbowOut = 0.3f };
}

/// <summary>A prop held in a hand bone: the grip (position, rotation x y z w) in the bone's frame (Synty weapons: grip at the origin), and the model turned and shifted onto it.</summary>
public sealed class GangGrip
{
    public string Bone { get; set; } = "Hand_R";
    public float[] Position { get; set; } = { -0.0879f, 0.0391f, 0.0163f };
    public float[] Rotation { get; set; } = { -0.4785f, -0.3531f, 0.3529f, 0.7224f };
    public float[] ModelRotationDeg { get; set; } = { 0, 0, 0 };
    public float[] ModelOffset { get; set; } = { 0, 0, 0 };
}

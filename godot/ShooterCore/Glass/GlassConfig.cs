namespace Drycastle.ShooterCore;

/// <summary>
/// glass.json: the tuning of glass panes (ADR 0007). Optional: a data directory without the file gets these defaults.
/// The server reads the hit points, the reach and the blast numbers; the client reads the crack stages and the shard numbers.
/// </summary>
public sealed class GlassConfig : IVersioned
{
    public int Version { get; set; } = 1;
    /// <summary>Hit points of a building's pane (a window of the base map, windows.json).</summary>
    public float BuildingPaneHp { get; set; } = 60;
    /// <summary>Hit points of a vehicle's pane (vehicles.json "glass"), unless the pane sets its own.</summary>
    public float VehiclePaneHp { get; set; } = 60;
    /// <summary>
    /// HP fractions at which a damaged pane's crack grows, highest first (as vehicles.json damage_stages). A pane below full HP is at
    /// crack stage 1; each fraction it has fallen to adds one. With [0.66, 0.33] there are three crack stages before the pane shatters.
    /// </summary>
    public List<float> CrackStages { get; set; } = new() { 0.66f, 0.33f };
    /// <summary>Damage a blast does to a pane at its centre, falling linearly to nothing at the blast's radius.</summary>
    public float BlastDamage { get; set; } = 150;
    /// <summary>How far behind the wall a bullet stopped on a building's pane may lie, metres (a pane sits in its reveal, behind the collision wall).</summary>
    public float BuildingReach { get; set; } = 0.5f;
    /// <summary>How far inside a vehicle's hit box a pane may lie along the bullet, metres (the windscreen is behind the bonnet's part of the box).</summary>
    public float VehicleReach { get; set; } = 2.5f;
    /// <summary>Whether a shot through a shattered vehicle pane can hit the riders behind it. False keeps every rider behind the hull, as before glass.</summary>
    public bool ShatteredExposesRiders { get; set; } = true;
    /// <summary>Whether a new round starts with every pane whole again.</summary>
    public bool RestoreOnRoundStart { get; set; } = true;
    /// <summary>Client: about how many fragments a shattering pane breaks into.</summary>
    public int Shards { get; set; } = 28;
    /// <summary>Client: speed of the fragments away from the pane, m/s.</summary>
    public float ShardSpeed { get; set; } = 3.5f;
    /// <summary>Client: seconds a fragment flies before it is gone.</summary>
    public float ShardLife { get; set; } = 1.3f;
    /// <summary>Client: shatter animations that may run at once; a pane that shatters past this just changes to its shattered look.</summary>
    public int MaxShatterEffects { get; set; } = 12;
    /// <summary>Client: beyond this many metres from the camera a shatter is not animated.</summary>
    public float ShatterEffectRange { get; set; } = 150;

    public static GlassConfig Parse(string json) => DataFile.Parse<GlassConfig>(json, "glass.json").Validate();

    public static GlassConfig Load(string path) => DataFile.Load<GlassConfig>(path).Validate();

    GlassConfig Validate()
    {
        if (!(BuildingPaneHp > 0) || !(VehiclePaneHp > 0)) throw new DataFileException("glass.json: building_pane_hp and vehicle_pane_hp must be positive");
        if (CrackStages.Count > 6) throw new DataFileException("glass.json: at most 6 crack_stages");
        for (var i = 0; i < CrackStages.Count; i++)
            if (!(CrackStages[i] > 0 && CrackStages[i] < 1) || i > 0 && CrackStages[i] >= CrackStages[i - 1])
                throw new DataFileException("glass.json: crack_stages are fractions between 0 and 1, highest first");
        if (BlastDamage < 0 || BuildingReach < 0 || VehicleReach < 0) throw new DataFileException("glass.json: blast_damage, building_reach and vehicle_reach must not be negative");
        if (Shards is < 2 or > 200) throw new DataFileException("glass.json: shards must be 2..200");
        if (!(ShardSpeed > 0) || !(ShardLife > 0)) throw new DataFileException("glass.json: shard_speed and shard_life must be positive");
        if (MaxShatterEffects < 0 || ShatterEffectRange < 0) throw new DataFileException("glass.json: max_shatter_effects and shatter_effect_range must not be negative");
        return this;
    }
}

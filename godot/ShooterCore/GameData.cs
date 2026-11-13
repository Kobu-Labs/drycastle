namespace Drycastle.ShooterCore;

/// <summary>All data files, loaded from one directory. Levels are checked against the weapons and perks, so it loads last.</summary>
public class GameData
{
    public required WeaponsFile Weapons { get; init; }
    public required MatchConfig Match { get; init; }
    public required VehiclesFile Vehicles { get; init; }
    public required PerksFile Perks { get; init; }
    public required LevelsFile Levels { get; init; }
    /// <summary>killstreaks.json: the streak table (kill thresholds, unlock levels, rules).</summary>
    public required KillstreakFile Killstreaks { get; init; }

    /// <summary>environment.json (day/night clock). Defaults when the file is absent from the parse call.</summary>
    public EnvironmentConfig Environment { get; init; } = new();
    /// <summary>glass.json (glass pane hit points, crack stages, shards; ADR 0007). Defaults when the file is absent.</summary>
    public GlassConfig Glass { get; init; } = new();
    /// <summary>animals.json (the species of wildlife and livestock and their rules). Empty when the file is absent.</summary>
    public AnimalsFile Animals { get; init; } = new();
    /// <summary>items.json (equippable items: headgear and the event rewards). Empty when the file is absent.</summary>
    public ItemsFile Items { get; init; } = new();
    /// <summary>bigfoot.json (every number of the Bigfoot event, issue #264). The defaults of the file when it is absent.</summary>
    public BigfootFile Bigfoot { get; init; } = new();
    /// <summary>turret.json: the sentry turret's tuning (the turret killstreak).</summary>
    public required TurretFile Turret { get; init; }

    /// <summary>camo.json (camouflage presets, tuft and concealment rules). A built-in minimal file when absent.</summary>
    public CamoFile Camo { get; init; } = CamoFile.Parse(MinimalCamoJson);

    /// <summary>Used when camo.json is absent (headless hosts and old data directories): one body, one skin tone, one suit, one face preset, no tufts.</summary>
    const string MinimalCamoJson = """
        {"version":1,"suit_patterns":["blotch"],"face_patterns":["bars"],"vegetation":["grass"],"headgear":["standard","suit_cover","none"],
        "bodies":[{"id":"soldier_m","name":"Soldier (M)","model":"animated-characters/soldier-male.glb","suit":["Main","DarkGreen"],"gear":["Black"],"headgear":["Helmet"]}],
        "skin_tones":[{"id":"light","name":"Light","colour":"#e0ac84"}],"tuft_max_layers":0,
        "tuft_size_m":[0,0],"tuft_sway_m":[0,0],"pattern_scale_per_m":[1,1],"spotting_min_multiplier":0.35,
        "concealment":{"stand":0.3,"crouch":0.6,"prone":0.9,"stillness_weight":0.7,"full_speed_mps":6},
        "suit_presets":[{"id":"olive","name":"Olive","base":"#4b5a32","secondary":"#2e3a1f","pattern":"blotch","vegetation":"grass"}],
        "face_presets":[{"id":"none","name":"None","pattern":"bars","colours":["#1d2a14","#5b6b3a","#c8b98a"],"coverage":0}]}
        """;

    /// <summary>For hosts that read files themselves (Godot res:// inside an export).</summary>
    public static GameData Parse(string weaponsJson, string matchJson, string vehiclesJson, string perksJson, string levelsJson, string killstreaksJson, string turretJson, string environmentJson = "", string camoJson = "", string glassJson = "", string animalsJson = "", string itemsJson = "", string bigfootJson = "")
    {
        var weapons = WeaponsFile.Parse(weaponsJson);
        var perks = PerksFile.Parse(perksJson);
        var killstreaks = KillstreakFile.Parse(killstreaksJson);
        return new()
        {
            Weapons = weapons,
            Match = DataFile.Parse<MatchConfig>(matchJson, "match.json"),
            Vehicles = VehiclesFile.Validate(DataFile.Parse<VehiclesFile>(vehiclesJson, "vehicles.json")),
            Perks = perks,
            Killstreaks = killstreaks,
            Turret = TurretFile.Parse(turretJson),
            Levels = LevelsFile.Parse(levelsJson, weapons, perks, killstreaks.UnlockLevels),
            Environment = environmentJson == "" ? new() : EnvironmentConfig.Parse(environmentJson),
            Camo = camoJson == "" ? CamoFile.Parse(MinimalCamoJson) : CamoFile.Parse(camoJson),
            Glass = glassJson == "" ? new() : GlassConfig.Parse(glassJson),
            Animals = animalsJson == "" ? new() : AnimalsFile.Parse(animalsJson),
            Items = itemsJson == "" ? new() : ItemsFile.Parse(itemsJson),
            Bigfoot = bigfootJson == "" ? new() : BigfootFile.Parse(bigfootJson),
        };
    }

    public static GameData Load(string dir)
    {
        var weapons = WeaponsFile.Load(Path.Combine(dir, "weapons.json"));
        var perks = PerksFile.Load(Path.Combine(dir, "perks.json"));
        var killstreaks = KillstreakFile.Load(Path.Combine(dir, "killstreaks.json"));
        return new()
        {
            Weapons = weapons,
            Match = MatchConfig.Load(Path.Combine(dir, "match.json")),
            Vehicles = VehiclesFile.Load(Path.Combine(dir, "vehicles.json")),
            Perks = perks,
            Killstreaks = killstreaks,
            Turret = TurretFile.Load(Path.Combine(dir, "turret.json")),
            Levels = LevelsFile.Load(Path.Combine(dir, "levels.json"), weapons, perks, killstreaks.UnlockLevels),
            Environment = File.Exists(Path.Combine(dir, "environment.json")) ? EnvironmentConfig.Load(Path.Combine(dir, "environment.json")) : new(),
            Camo = File.Exists(Path.Combine(dir, "camo.json")) ? CamoFile.Load(Path.Combine(dir, "camo.json")) : CamoFile.Parse(MinimalCamoJson),
            Glass = File.Exists(Path.Combine(dir, "glass.json")) ? GlassConfig.Load(Path.Combine(dir, "glass.json")) : new(),
            Animals = File.Exists(Path.Combine(dir, "animals.json")) ? AnimalsFile.Load(Path.Combine(dir, "animals.json")) : new(),
            Items = File.Exists(Path.Combine(dir, "items.json")) ? ItemsFile.Load(Path.Combine(dir, "items.json")) : new(),
            Bigfoot = File.Exists(Path.Combine(dir, "bigfoot.json")) ? BigfootFile.Load(Path.Combine(dir, "bigfoot.json")) : new(),
        };
    }
}

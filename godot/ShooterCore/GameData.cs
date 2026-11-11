namespace Suchohrad.ShooterCore;

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

    /// <summary>For hosts that read files themselves (Godot res:// inside an export).</summary>
    public static GameData Parse(string weaponsJson, string matchJson, string vehiclesJson, string perksJson, string levelsJson, string killstreaksJson, string environmentJson = "")
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
            Levels = LevelsFile.Parse(levelsJson, weapons, perks, killstreaks.UnlockLevels),
            Environment = environmentJson == "" ? new() : EnvironmentConfig.Parse(environmentJson),
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
            Levels = LevelsFile.Load(Path.Combine(dir, "levels.json"), weapons, perks, killstreaks.UnlockLevels),
            Environment = File.Exists(Path.Combine(dir, "environment.json")) ? EnvironmentConfig.Load(Path.Combine(dir, "environment.json")) : new(),
        };
    }
}

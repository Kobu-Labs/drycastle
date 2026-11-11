namespace Suchohrad.ShooterCore;

/// <summary>All three data files, loaded from one directory.</summary>
public class GameData
{
    public required WeaponsFile Weapons { get; init; }
    public required MatchConfig Match { get; init; }
    public required VehiclesFile Vehicles { get; init; }
    public required PerksFile Perks { get; init; }

    /// <summary>For hosts that read files themselves (Godot res:// inside an export).</summary>
    public static GameData Parse(string weaponsJson, string matchJson, string vehiclesJson, string perksJson) => new()
    {
        Weapons = WeaponsFile.Parse(weaponsJson),
        Match = DataFile.Parse<MatchConfig>(matchJson, "match.json"),
        Vehicles = VehiclesFile.Validate(DataFile.Parse<VehiclesFile>(vehiclesJson, "vehicles.json")),
        Perks = PerksFile.Parse(perksJson),
    };

    public static GameData Load(string dir) => new()
    {
        Weapons = WeaponsFile.Load(Path.Combine(dir, "weapons.json")),
        Match = MatchConfig.Load(Path.Combine(dir, "match.json")),
        Vehicles = VehiclesFile.Load(Path.Combine(dir, "vehicles.json")),
        Perks = PerksFile.Load(Path.Combine(dir, "perks.json")),
    };
}

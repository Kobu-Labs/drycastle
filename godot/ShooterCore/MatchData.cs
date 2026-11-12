namespace Drycastle.ShooterCore;

public enum MatchMode : byte { FreeForAll = 0, TeamDeathmatch = 1 }

/// <summary>match.json. Values from the FFA match loop ticket (#72).</summary>
public class MatchConfig : IVersioned
{
    public int Version { get; set; }
    public int ScoreGoal { get; set; } = 25;
    public float TimeLimit { get; set; } = 600;
    public float RespawnDelay { get; set; } = 3;
    public float SpawnProtection { get; set; } = 3;
    public float EndScreenTime { get; set; } = 10;
    public float MinSpawnDistance { get; set; } = 40;
    public int MinPlayers { get; set; } = 1;
    /// <summary>Stationary target dummies planted around the spawn area (#148). 0 in normal matches; the server's --targets sets it.</summary>
    public int Targets { get; set; } = 0;
    /// <summary>Whether a target kill gives the shooter XP (so progression can be tested with dummies).</summary>
    public bool TargetsAwardXp { get; set; } = true;

    /// <summary>Game mode: "ffa" (free-for-all, the default) or "tdm" (team deathmatch, two teams). See <see cref="ParsedMode"/>.</summary>
    public string Mode { get; set; } = "ffa";
    /// <summary>Team deathmatch only: whether a player's shots hurt teammates. Off by default; self damage always applies.</summary>
    public bool FriendlyFire { get; set; } = false;
    /// <summary>Team deathmatch only: the team kill total that ends the round (a team shares the goal, so it is higher than <see cref="ScoreGoal"/>).</summary>
    public int TeamScoreGoal { get; set; } = 75;

    /// <summary><see cref="Mode"/> as an enum; an unknown name is a data error, not a silent fall back to FFA.</summary>
    public MatchMode ParsedMode() => Mode.Trim().ToLowerInvariant() switch
    {
        "ffa" => MatchMode.FreeForAll,
        "tdm" => MatchMode.TeamDeathmatch,
        _ => throw new DataFileException($"match.json: unknown mode \"{Mode}\" (expected \"ffa\" or \"tdm\")"),
    };

    public static MatchConfig Load(string path) => DataFile.Load<MatchConfig>(path);
}

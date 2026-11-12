namespace Drycastle.ShooterCore;

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

    public static MatchConfig Load(string path) => DataFile.Load<MatchConfig>(path);
}

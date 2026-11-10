namespace Suchohrad.ShooterCore;

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

    public static MatchConfig Load(string path) => DataFile.Load<MatchConfig>(path);
}

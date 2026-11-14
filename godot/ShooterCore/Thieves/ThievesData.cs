namespace Drycastle.ShooterCore;

/// <summary>
/// thieves.json, schema version 1: every number of the Thieves event (GLOSSARY "Thieves event"). Only the version so far; the Raid counts, the
/// Gang members' Hit counts, the van and the Loot caps arrive with the tickets that need them (docs/events/thieves/).
/// </summary>
public sealed class ThievesFile : IVersioned
{
    public int Version { get; set; }

    public static ThievesFile Load(string path) => DataFile.Load<ThievesFile>(path);

    public static ThievesFile Parse(string json) => DataFile.Parse<ThievesFile>(json, "thieves.json");
}

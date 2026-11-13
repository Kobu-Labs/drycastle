namespace Drycastle.Shooter;

/// <summary>English strings of the tactical map (the M key). Registered with <see cref="Strings.Register"/>. Keys are prefixed map. and bind.map.</summary>
public static class TacticalMapStrings
{
    public static readonly (string Key, string Text)[] Table =
    {
        ("bind.map", "Tactical map (toggle)"),
        ("map.grid", "GRID REFERENCE"),
        ("map.waypoint", "Target - {0}"),
        ("map.none", "--"),
        ("map.dir.0", "N"), ("map.dir.1", "NE"), ("map.dir.2", "E"), ("map.dir.3", "SE"),
        ("map.dir.4", "S"), ("map.dir.5", "SW"), ("map.dir.6", "W"), ("map.dir.7", "NW"),
        ("map.unavailable", "No map data loaded"),
        ("map.unavailable.note", "Your position and heading are still shown."),
    };
}

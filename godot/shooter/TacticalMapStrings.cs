namespace Drycastle.Shooter;

/// <summary>English strings of the tactical map (the M key). Registered with <see cref="Strings.Register"/>. Keys are prefixed map. and bind.map.</summary>
public static class TacticalMapStrings
{
    public static readonly (string Key, string Text)[] Table =
    {
        ("bind.map", "Tactical map (toggle)"),
        ("map.title", "TACTICAL MAP"),
        ("map.subtitle", "North up   {0} m grid"),
        ("map.grid", "GRID REFERENCE"),
        ("map.position", "POSITION"),
        ("map.position.value", "E {0}   N {1}"),
        ("map.position.note", "Metres from SW corner"),
        ("map.heading", "HEADING"),
        ("map.heading.value", "{0:000}°  {1}"),
        ("map.none", "--"),
        ("map.dir.0", "N"), ("map.dir.1", "NE"), ("map.dir.2", "E"), ("map.dir.3", "SE"),
        ("map.dir.4", "S"), ("map.dir.5", "SW"), ("map.dir.6", "W"), ("map.dir.7", "NW"),
        ("map.legend", "LEGEND"),
        ("map.legend.you", "You (facing arrow)"),
        ("map.legend.vehicle", "Vehicle"),
        ("map.legend.friend", "Friend (team match)"),
        ("map.legend.enemy", "Enemy contact (UAV)"),
        ("map.scale", "SCALE"),
        ("map.scale.m", "{0} m"),
        ("map.hint", "{0} or Esc  Close map"),
        ("map.hint.teleport", "Middle click  Teleport (sandbox)"),
        ("map.unavailable", "No map data loaded"),
        ("map.unavailable.note", "Your position and heading are still shown."),
    };
}

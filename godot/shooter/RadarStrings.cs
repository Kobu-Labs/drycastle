namespace Drycastle.Shooter;

/// <summary>English strings of the radar unit's screen and prompts (the tactical map as the operator's radar, GLOSSARY "Radar unit"). Registered with <see cref="Strings.Register"/>. Keys are prefixed radar. and hud.radar. / hud.seat.radar_operator.</summary>
public static class RadarStrings
{
    public static readonly (string Key, string Text)[] Table =
    {
        ("radar.title", "RADAR UNIT"),
        ("radar.unit", "UNIT GRID REFERENCE"),
        ("radar.status.live", "SWEEPING"),
        ("radar.status.none", "NO SIGNAL"),
        ("radar.contacts", "ENEMY CONTACTS  {0}"),
        ("radar.cursor", "CURSOR GRID REFERENCE"),
        ("radar.legend", "Diamond: enemy on foot. Crossed square: enemy vehicle. Disc: friendly. An enemy fades one turn after the arm passed it."),
        ("radar.hint", "Arrows or D-pad: move the cursor and read its grid reference to your crew. Wheel, +/-: zoom. Esc: close the screen."),
        ("hud.seat.radar_operator", "Radar operator"),
        ("hud.radar.enter", "{0}  Man the radar unit"),
        ("hud.radar.prompt", "RADAR  {0}  Screen     {1}  Leave the radar unit"),
    };
}

namespace Suchohrad.Shooter;

/// <summary>
/// Strings for the HUD leftovers and minimap settings (ticket #143). Kept in their own file so parallel work on
/// <see cref="Strings"/> stays small; <see cref="Strings.Register"/> adds this table too. Keys are prefixed hud2.
/// </summary>
public static class Hud2Strings
{
    public static readonly (string Key, string Text)[] Table =
    {
        ("hud2.progress.level", "Level {0}"),
        ("hud2.progress.xp", "{0} / {1} XP"),
        ("hud2.progress.max", "Max level  {0} XP"),
        ("hud2.perk", "Perk: {0}"),
        ("hud2.set.minimap.size", "Minimap size"),
        ("hud2.set.minimap.opacity", "Minimap opacity"),
        ("hud2.set.minimap.zoom", "Minimap zoom"),
        ("hud2.fmt.px", "{0} px"),
        ("hud2.fmt.metres", "{0} m"),
    };
}

namespace Drycastle.ShooterCore;

/// <summary>
/// Friend and enemy colours of team deathmatch (kill feed, scoreboard, markers, minimap) as plain sRGB triples, so the
/// colour-blind rules are unit tested without Godot. Friend is blue and enemy is orange in both sets (a blue/orange pair stays
/// apart under red-green colour blindness, unlike green/red); the colour-blind set is pushed further apart in lightness.
/// Colour is never the only signal: the HUD also shows the team tag ("A"/"B") and "You", see docs/standards/ui-ux.md section 4.
/// Both sets reach 4.5:1 contrast against the HUD panel colour.
/// </summary>
public static class TeamPalette
{
    public static StatePalette.Rgb Friend(bool colorBlind) => colorBlind ? new StatePalette.Rgb(0.30f, 0.55f, 1.00f) : new StatePalette.Rgb(0.45f, 0.72f, 1.00f);
    public static StatePalette.Rgb Enemy(bool colorBlind) => colorBlind ? new StatePalette.Rgb(1.00f, 0.68f, 0.12f) : new StatePalette.Rgb(1.00f, 0.45f, 0.22f);
}

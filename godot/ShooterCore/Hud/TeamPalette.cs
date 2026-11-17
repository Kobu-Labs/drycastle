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
    /// <summary>
    /// A squad mate on the minimap and the tactical map (#379): blue, where the other teammates are <see cref="StatePalette.Good"/> green. The colour-blind set is a deeper blue so it stays apart from the
    /// blue-green of that set in lightness too. The map also says it in shape and words (a ringed disc with the name), never colour alone; it reaches 3:1 against the HUD panel.
    /// </summary>
    public static StatePalette.Rgb Squad(bool colorBlind) => colorBlind ? new StatePalette.Rgb(0.25f, 0.45f, 1.00f) : Friend(false);
    public static StatePalette.Rgb Enemy(bool colorBlind) => colorBlind ? new StatePalette.Rgb(1.00f, 0.68f, 0.12f) : new StatePalette.Rgb(1.00f, 0.45f, 0.22f);
}

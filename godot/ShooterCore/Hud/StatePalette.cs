namespace Suchohrad.ShooterCore;

/// <summary>
/// The HUD's state colours as plain sRGB triples (0..1), so the colour-blind set can be unit tested without Godot.
/// <c>ShooterTheme</c> turns these into Godot colours; docs/standards/ui-ux.md section 4 (never colour-only, colour-blind-safe option).
/// The colour-blind set is blue-green / yellow / vermillion plus the cyan focus colour, separated in hue and in lightness.
/// </summary>
public static class StatePalette
{
    public readonly record struct Rgb(float R, float G, float B);

    public static readonly Rgb Accent = new(0.40f, 0.82f, 1f);

    public static Rgb Good(bool colorBlind) => colorBlind ? new Rgb(0.05f, 0.66f, 0.48f) : new Rgb(0.45f, 0.90f, 0.55f);
    public static Rgb Warn(bool colorBlind) => colorBlind ? new Rgb(0.94f, 0.89f, 0.26f) : new Rgb(1f, 0.74f, 0.30f);
    public static Rgb Danger(bool colorBlind) => colorBlind ? new Rgb(0.90f, 0.40f, 0.10f) : new Rgb(1f, 0.42f, 0.38f);
}

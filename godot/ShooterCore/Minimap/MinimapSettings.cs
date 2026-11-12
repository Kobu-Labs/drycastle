namespace Drycastle.ShooterCore;

/// <summary>
/// The minimap's settings (ticket #143): size in pixels, opacity and zoom (the half-width of the view in metres, see
/// <see cref="MinimapMath.PxPerMetre"/>). The ranges and clamps live here so the settings screen, the file loader and the
/// HUD agree; Godot-free and unit tested.
/// </summary>
public static class MinimapSettings
{
    public const float DefaultSize = 224, SizeMin = 160, SizeMax = 320;
    public const float DefaultOpacity = 1f, OpacityMin = 0.3f, OpacityMax = 1f;
    public const float DefaultZoom = 90, ZoomMin = 40, ZoomMax = 200;

    public static float ClampSize(float v) => Math.Clamp(v, SizeMin, SizeMax);
    public static float ClampOpacity(float v) => Math.Clamp(v, OpacityMin, OpacityMax);
    public static float ClampZoom(float v) => Math.Clamp(v, ZoomMin, ZoomMax);
}

using System.Numerics;

namespace Drycastle.ShooterCore.Minimap;

/// <summary>
/// Godot-free zoom and pan of the tactical map. The view is a window onto the square sheet: <c>zoom</c> (1 = the whole sheet) and the
/// window's top-left corner <c>topLeft</c> as a fraction of the sheet (0..1, from the west and north edges). The window is
/// <c>1 / zoom</c> of the sheet wide, and never leaves it.
/// </summary>
public static class TacticalMapView
{
    public const float MinZoom = 1f, MaxZoom = 4f;
    /// <summary>Zoom factor of one wheel notch or key press.</summary>
    public const float Step = 1.25f;

    public static float ClampZoom(float zoom) => Math.Clamp(zoom, MinZoom, MaxZoom);

    /// <summary>The top-left corner kept inside the sheet for this zoom.</summary>
    public static Vector2 Clamp(Vector2 topLeft, float zoom)
    {
        var max = 1f - 1f / ClampZoom(zoom);
        return new Vector2(Math.Clamp(topLeft.X, 0f, max), Math.Clamp(topLeft.Y, 0f, max));
    }

    /// <summary>Sheet fraction under a window fraction (0..1 across the visible window).</summary>
    public static Vector2 ToSheet(Vector2 window, Vector2 topLeft, float zoom) => topLeft + window / zoom;

    /// <summary>Window fraction of a sheet fraction (outside 0..1 when it is off screen). Inverse of <see cref="ToSheet"/>.</summary>
    public static Vector2 ToWindow(Vector2 sheet, Vector2 topLeft, float zoom) => (sheet - topLeft) * zoom;

    /// <summary>Zooms by <paramref name="factor"/> keeping the sheet point under <paramref name="anchor"/> (a window fraction) where it is.</summary>
    public static (float Zoom, Vector2 TopLeft) ZoomAt(float zoom, Vector2 topLeft, Vector2 anchor, float factor)
    {
        var z = ClampZoom(zoom * factor);
        var under = ToSheet(anchor, topLeft, zoom);
        return (z, Clamp(under - anchor / z, z));
    }

    /// <summary>Moves the window by <paramref name="windowDelta"/> (window fractions: 0.5 = half the visible window).</summary>
    public static Vector2 Pan(Vector2 topLeft, float zoom, Vector2 windowDelta) => Clamp(topLeft + windowDelta / zoom, zoom);
}

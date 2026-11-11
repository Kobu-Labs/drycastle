using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The enemy markers of the point-of-view overlays (tickets #157 and #159): a red diamond outline on each visible enemy point
/// (a player's chest, a target dummy's chest), sized by distance and clamped, with the one nearest the reticle brighter and
/// larger. The gunship overlay and the attack missile's overlay both draw through this one helper, so they cannot drift apart.
/// Points behind the camera or off the screen are not drawn. The pulse is off with reduce motion. Nothing is allocated per
/// call: the four corners are a reused array (the callers draw on the UI thread only).
/// </summary>
public static class EnemyMarkers
{
    /// <summary>Half the diamond's diagonal is MarkerScaleM over the distance, clamped (pixels at 1080 p).</summary>
    public const float MarkerScaleM = 900f, MarkerMinPx = 6f, MarkerMaxPx = 22f;
    /// <summary>A diamond counts as the aimed one only within this distance of the reticle (pixels at 1080 p).</summary>
    public const float AimRadiusPx = 60f;

    /// <summary>Corners of one diamond, reused every draw.</summary>
    static readonly Vector2[] Corners = new Vector2[5];

    /// <summary>
    /// Draws the markers onto <paramref name="canvas"/>. <paramref name="centre"/> is the reticle, <paramref name="size"/> the
    /// canvas size in pixels, <paramref name="s"/> the screen scale (height over 1080 p).
    /// </summary>
    public static void Draw(CanvasItem canvas, Camera3D camera, IReadOnlyList<Vector3> points, Vector2 centre, Vector2 size, float s)
    {
        if (points.Count == 0) return;
        var pulse = GameSettings.Current.ReduceMotion ? 1f : 0.5f + 0.5f * Mathf.Sin((float)(Time.GetTicksMsec() / 1000.0 * Mathf.Tau * 1.5));
        // pass 1: the visible points, and the one nearest the reticle (the aimed one)
        var aimedIndex = -1; var aimedD = float.MaxValue;
        for (var i = 0; i < points.Count; i++)
        {
            if (ProjectVisible(camera, points[i], size) is not { } at) continue;
            var d = at.DistanceTo(centre);
            if (d < aimedD) { aimedD = d; aimedIndex = i; }
        }
        // pass 2: draw them; the aimed one brighter and larger
        for (var i = 0; i < points.Count; i++)
        {
            if (ProjectVisible(camera, points[i], size) is not { } at) continue;
            var half = HalfSize(camera, points[i], s);
            if (i == aimedIndex && aimedD <= AimRadiusPx * s) DrawDiamond(canvas, at, half * 1.35f, true, pulse);
            else DrawDiamond(canvas, at, half, false, pulse);
        }
    }

    /// <summary>The screen position of a point, or null when it is behind the camera or off the screen.</summary>
    static Vector2? ProjectVisible(Camera3D camera, Vector3 p, Vector2 size)
    {
        if (camera.IsPositionBehind(p)) return null;
        var at = camera.UnprojectPosition(p);
        return at.X < 0 || at.Y < 0 || at.X > size.X || at.Y > size.Y ? null : at;
    }

    /// <summary>Half the diamond's diagonal for a point: MarkerScaleM over its distance, clamped to the min and max (scaled by screen height).</summary>
    static float HalfSize(Camera3D camera, Vector3 p, float s)
    {
        var dist = camera.GlobalPosition.DistanceTo(p);
        return Mathf.Clamp(MarkerScaleM / Mathf.Max(dist, 1f), MarkerMinPx, MarkerMaxPx) * s;
    }

    /// <summary>One diamond outline around <paramref name="at"/>; <paramref name="aimed"/> draws it brighter and thicker.</summary>
    static void DrawDiamond(CanvasItem canvas, Vector2 at, float half, bool aimed, float pulse)
    {
        Corners[0] = at + new Vector2(0, -half);
        Corners[1] = at + new Vector2(half, 0);
        Corners[2] = at + new Vector2(0, half);
        Corners[3] = at + new Vector2(-half, 0);
        Corners[4] = Corners[0];
        var width = (aimed ? 3f : 2f) * (0.8f + 0.2f * pulse);
        canvas.DrawPolyline(Corners, aimed ? Colors.White.Lerp(ShooterTheme.Danger, 0.35f) : ShooterTheme.Danger, width, true);
    }
}

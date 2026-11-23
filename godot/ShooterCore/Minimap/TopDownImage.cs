using System.Globalization;
using System.Text.Json;

namespace Drycastle.ShooterCore.Minimap;

/// <summary>
/// Where the map's top-down render (<c>tactical-map.png</c>, made by the viewer's <c>--topdown</c>) sits on the ground, from its
/// sidecar <c>tactical-map.png.json</c>: the ground x and z of its north-west corner (north is -z), its size in pixels and its
/// metres per pixel. Godot-free; the tactical map's terrain texture is built from the picture in <c>MapBase</c>.
/// </summary>
public readonly record struct TopDownImage(float OriginX, float OriginZ, int WidthPx, int HeightPx, float MetresPerPx)
{
    public const string PngName = "tactical-map.png", JsonName = PngName + ".json";

    /// <summary>The sidecar's extent, or null when the text is not a usable one (missing field, non-positive size or scale).</summary>
    public static TopDownImage? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (!r.TryGetProperty("origin_x", out var ox) || !r.TryGetProperty("origin_z", out var oz)
                || !r.TryGetProperty("width_px", out var w) || !r.TryGetProperty("height_px", out var h)
                || !r.TryGetProperty("metres_per_px", out var m)) return null;
            var t = new TopDownImage(ox.GetSingle(), oz.GetSingle(), w.GetInt32(), h.GetInt32(), m.GetSingle());
            return t.WidthPx > 0 && t.HeightPx > 0 && t.MetresPerPx > 0f && float.IsFinite(t.OriginX) && float.IsFinite(t.OriginZ) ? t : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>
    /// The picture's rectangle in the texels of a square terrain texture that covers <paramref name="sideMetres"/> from ground
    /// (<paramref name="extentX"/>, <paramref name="extentZ"/>) at <paramref name="side"/> texels: left, top, width, height.
    /// Texel (0, 0) is the extent's north-west corner, like the picture's own.
    /// </summary>
    public (float X, float Y, float W, float H) Placement(float extentX, float extentZ, float sideMetres, int side)
    {
        var texelsPerMetre = side / sideMetres;
        return ((OriginX - extentX) * texelsPerMetre, (OriginZ - extentZ) * texelsPerMetre,
                WidthPx * MetresPerPx * texelsPerMetre, HeightPx * MetresPerPx * texelsPerMetre);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{WidthPx}x{HeightPx} px at {MetresPerPx} m/px from ({OriginX}, {OriginZ})");
}

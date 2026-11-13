using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.MapLoader;

/// <summary>
/// The look of one water body: the murky palette (olive shallows with some sediment to dark brown-green deep water),
/// how far the bed sinks into the deep colour, the wave size and the flow. Read by <see cref="WaterSurfaces"/> (one
/// shader material per body) and by the boats' visual bob. Rivers differ slightly from ponds and lakes.
/// </summary>
public readonly record struct WaterPalette(Color Shallow, Color Deep, float MurkDensity, float WaveCap, Vector2 Flow);

public static class WaterPalettes
{
    /// <summary>The Morava chain: the river parcels (flowing; the current comes with slice 4).</summary>
    public static readonly string[] RiverIds = { "P-0901", "P-0534", "P-1180", "P-1184", "P-1378", "P-1597", "P-1596" };

    /// <summary>Rivers: a little more sediment and clearer shallows (the bed reads through more).</summary>
    public static readonly WaterPalette River = new(
        Shallow: new Color(0.50f, 0.47f, 0.24f),
        Deep: new Color(0.15f, 0.13f, 0.07f),
        MurkDensity: 0.45f,
        WaveCap: 0.10f,
        Flow: Vector2.Zero);

    /// <summary>Lakes and ponds: darker, more murk, smaller waves.</summary>
    public static readonly WaterPalette Still = new(
        Shallow: new Color(0.44f, 0.45f, 0.22f),
        Deep: new Color(0.12f, 0.12f, 0.06f),
        MurkDensity: 0.60f,
        WaveCap: 0.05f,
        Flow: Vector2.Zero);

    public static bool IsRiver(string id) => Array.IndexOf(RiverIds, id) >= 0;

    public static WaterPalette For(WaterBody body) => IsRiver(body.Id) ? River : Still;

    /// <summary>Longer side of the body's bounding box (m): the size the wave amplitude scales with.</summary>
    public static float Extent(WaterBody body) => MathF.Max(body.MaxX - body.MinX, body.MaxZ - body.MinZ);

    /// <summary>Wave amplitude (m) of a body, 0 with reduce motion. Boats bob with the same amplitude as the shader's surface.</summary>
    public static float WaveAmplitude(WaterBody body)
    {
        if (WaterSurfaces.ReduceMotion()) return 0f;
        return WaterWaves.Amplitude(Extent(body), For(body).WaveCap);
    }
}

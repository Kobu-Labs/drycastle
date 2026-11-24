using Godot;
using Drycastle.ShooterCore.Waters;

namespace Drycastle.MapLoader.Waters;

/// <summary>
/// The look of one water body: the murky palette (olive shallows with some sediment to dark brown-green deep water),
/// the wave size and the flow. Read by <see cref="WaterSurfaces"/> (one
/// shader material per body) and by the boats' visual bob. Rivers differ slightly from ponds and lakes.
/// </summary>
public readonly record struct WaterPalette(Color Shallow, Color Deep, float WaveCap, Vector2 Flow);

public static class WaterPalettes
{
    /// <summary>The Morava chain: the river parcels (flowing; the current comes with slice 4).</summary>
    public static readonly string[] RiverIds = { "P-0901", "P-0534", "P-1180", "P-1184", "P-1378", "P-1597", "P-1596" };

    /// <summary>Rivers: a little more sediment and clearer shallows (the bed reads through more).</summary>
    public static readonly WaterPalette River = new(
        Shallow: new Color(0.50f, 0.47f, 0.24f),
        Deep: new Color(0.15f, 0.13f, 0.07f),
        WaveCap: 0.16f,
        Flow: Vector2.Zero);

    /// <summary>Lakes and ponds: darker, more murk, smaller waves.</summary>
    public static readonly WaterPalette Still = new(
        Shallow: new Color(0.44f, 0.45f, 0.22f),
        Deep: new Color(0.12f, 0.12f, 0.06f),
        WaveCap: 0.09f,
        Flow: Vector2.Zero);

    public static bool IsRiver(string id) => Array.IndexOf(RiverIds, id) >= 0;

    public static WaterPalette For(WaterBody body) => IsRiver(body.Id) ? River : Still;

    /// <summary>Longer side of the body's bounding box (m): the size the wave amplitude scales with.</summary>
    public static float Extent(WaterBody body) => MathF.Max(body.MaxX - body.MinX, body.MaxZ - body.MinZ);

    /// <summary>Depth (m) at which the waves reach their full height: they die out towards the bank, so the waterline stays put.</summary>
    public const float WaveFullDepth = 0.6f;

    /// <summary>Wave amplitude (m) of a body in open water. The surface mesh and the boats' bob use the same one.</summary>
    public static float WaveAmplitude(WaterBody body) => WaterWaves.Amplitude(Extent(body), For(body).WaveCap);

    /// <summary>Share of the amplitude at (x, z): 0 at the bank, 1 from <see cref="WaveFullDepth"/> of water (the shader's smoothstep).</summary>
    public static float WaveDamp(WaterBody body, float x, float z)
    {
        var t = Math.Clamp(body.DepthAt(x, z) / WaveFullDepth, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Height (m) of the waved surface above the body's flat surface at (x, z), now (<see cref="WaterSurfaces.WaveClock"/>).</summary>
    public static float WaveOffset(WaterBody body, float x, float z) =>
        WaterWaves.Height(x, z, (float)WaterSurfaces.WaveClock, WaveAmplitude(body) * WaveDamp(body, x, z));
}

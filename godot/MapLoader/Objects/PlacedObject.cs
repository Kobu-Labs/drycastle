using System.Text.Json;
using Godot;

namespace Drycastle.MapLoader.Objects;

/// <summary>A placed object (objects.json entry or parcel-run fragment object). Map metres, x east, y north.</summary>
public sealed class PlacedObject
{
    public string Id { get; init; } = "";
    public string Asset { get; init; } = "";
    public float X { get; init; }
    public float Y { get; init; }
    /// <summary>Elevation in metres; null = terrain height under the object.</summary>
    public float? Z { get; init; }
    public float RotationDeg { get; init; }
    public float Scale { get; init; } = 1f;
    /// <summary>Exact final bounding box in metres (X length along the object's x, Y width along map y, Z height); wins over <see cref="Scale"/>. Null = none.</summary>
    public Vector3? SizeM { get; init; }
    /// <summary>A GLB of the map (relative path, "graves/o-4673.glb") drawn in place of the asset's shared model, at its real size: never stretched
    /// by <see cref="SizeM"/>, which then only sizes the colliders. Null = the asset's model. Written by the export, not by edits.json.</summary>
    public string? Model { get; init; }
    public string? Colour { get; init; }
    /// <summary>False = no collision at all (players, vehicles and shots pass through): trash laid over a climbable prim. objects.json carries the key only when false.</summary>
    public bool Solid { get; init; } = true;
    /// <summary>Parcel containing the object (null: outside every parcel). An object belongs to no parcel; this is where it happens to stand.</summary>
    public string? Parcel { get; init; }
    /// <summary>The group node the object sits in (and is removed with): its containing parcel, else <see cref="NoParcelGroup"/>.</summary>
    public string Group => Parcel ?? NoParcelGroup;
    public const string NoParcelGroup = "_none";

    /// <summary>Reads an object's optional "size_m" [length, width, height]; null when absent or not three numbers above 0.</summary>
    public static Vector3? ReadSizeM(JsonElement o)
    {
        if (!o.TryGetProperty("size_m", out var a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 3) return null;
        var v = new float[3];
        for (var i = 0; i < 3; i++)
        {
            if (a[i].ValueKind != JsonValueKind.Number || !(a[i].GetSingle() > 0)) return null;
            v[i] = a[i].GetSingle();
        }
        return new Vector3(v[0], v[1], v[2]);
    }
}

/// <summary>An entry of assets/manifest.json: id ("pack:..." / "prim:..."), category, display name and size in metres (glTF axes).</summary>
public sealed record AssetInfo(string Id, string Category, string Name, Vector3 Size);

/// <summary>Result of <see cref="MapLoader.ApplyFragment"/>.</summary>
public sealed record FragmentResult(IReadOnlyList<string> Replaced, int Objects, int PropsNodes);

using System.Text.Json;
using Godot;

namespace Suchohrad.MapLoader;

/// <summary>A street lamp from lamps.json: the foot of its pole on the ground, Godot axes (Y up).</summary>
public sealed record LampSpot(string Id, Vector3 Position, string Source);

public sealed partial class MapLoader
{
    /// <summary>Street lamps (map 0.5.0+). Empty for older maps. The viewer ignores them; the shooter builds them.</summary>
    public IReadOnlyList<LampSpot> Lamps => _lamps;
    readonly List<LampSpot> _lamps = new();

    void ReadLamps(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var l in doc.RootElement.GetProperty("lamps").EnumerateArray())
        {
            _lamps.Add(new LampSpot(
                l.GetProperty("id").GetString()!,
                new Vector3(l.GetProperty("x").GetSingle(), l.GetProperty("z").GetSingle(), -l.GetProperty("y").GetSingle()),
                l.GetProperty("source").GetString() ?? ""));
        }
    }
}

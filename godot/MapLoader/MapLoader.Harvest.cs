using System.Text.Json;
using Godot;

namespace Drycastle.MapLoader;

/// <summary>
/// The harvester event's side of the crop fields (#331). The server reads the positions of the fields' own hay bales (<see cref="CropBales"/>, from crops.json, also on a headless load) so a shot can set
/// one alight. The client cuts the crop where the harvester has been (<see cref="CropCut"/>: plants in the swath are squashed to stubble, no plant is ever sent) and takes a burnt bale out of the field
/// (<see cref="CropHideArtifact"/>). All cosmetic, like the crops themselves.
/// </summary>
public sealed partial class MapLoader
{
    /// <summary>One MultiMesh of the crop layer with what a cut needs: its instances (map rows, so world = <c>ToGodot</c>) and which are cut already.</summary>
    sealed record CropCell(MultiMesh Mesh, string Kind, bool Plant, IReadOnlyList<ClutterInstance> Rows, bool[] Cut)
    {
        /// <summary>The cell's rows in game x, z (a swath that misses this box skips the cell).</summary>
        public readonly Vector2 Min = new(Rows.Min(r => r.X), Rows.Min(r => -r.Y)), Max = new(Rows.Max(r => r.X), Rows.Max(r => -r.Y));
    }

    readonly List<CropCell> _cropCells = new();

    /// <summary>The hay bales of the crop fields as game positions (x east, y up, z south): crops.json's <c>art_hay_bale*</c> rows. Empty without crops.json.</summary>
    public IReadOnlyList<Vector3> CropBales => _cropBales;
    readonly List<Vector3> _cropBales = new();

    /// <summary>Height a cut plant keeps (a fraction of its own): the stubble.</summary>
    public const float StubbleScale = 0.12f;

    void ReadCropBales(string mapDir, JsonElement manifest)
    {
        _cropBales.Clear();
        if (!manifest.TryGetProperty("files", out var files) || !files.TryGetProperty("crops", out var e) || e.ValueKind != JsonValueKind.String) return;
        var path = Path.Combine(mapDir, e.GetString()!);
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("chunks", out var chunks)) return;
            foreach (var chunk in chunks.EnumerateObject())
                foreach (var kind in chunk.Value.EnumerateObject())
                {
                    if (!kind.Name.StartsWith("art_hay_bale", StringComparison.Ordinal)) continue;
                    foreach (var row in kind.Value.EnumerateArray())
                        if (row.GetArrayLength() >= 3) _cropBales.Add(ToGodot(row[0].GetSingle(), row[1].GetSingle(), row[2].GetSingle()));
                }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { Warn($"crops: bales not read: {ex.Message}"); }
    }

    /// <summary>
    /// Squashes the crop plants within <paramref name="halfWidth"/> of the segment <paramref name="a"/>-<paramref name="b"/> (game x, z) to stubble. Each plant is cut once. Returns how many it cut.
    /// The cells near the segment are looked at, not the whole field.
    /// </summary>
    public int CropCut(Vector2 a, Vector2 b, float halfWidth)
    {
        var cut = 0;
        var min = new Vector2(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y)) - Vector2.One * halfWidth;
        var max = new Vector2(Mathf.Max(a.X, b.X), Mathf.Max(a.Y, b.Y)) + Vector2.One * halfWidth;
        foreach (var cell in _cropCells)
        {
            if (!cell.Plant || cell.Max.X < min.X || cell.Min.X > max.X || cell.Max.Y < min.Y || cell.Min.Y > max.Y || !GodotObject.IsInstanceValid(cell.Mesh)) continue;
            var rows = cell.Rows;
            for (var k = 0; k < rows.Count; k++)
            {
                if (cell.Cut[k]) continue;
                var r = rows[k];
                var x = r.X; var z = -r.Y; // map y north is game -z
                if (x < min.X || x > max.X || z < min.Y || z > max.Y) continue;
                if (DistanceToSegment(new Vector2(x, z), a, b) > halfWidth) continue;
                cell.Cut[k] = true;
                var basis = Basis.Identity.Rotated(Vector3.Up, r.Rot).Scaled(new Vector3(r.Scale, r.Scale * StubbleScale, r.Scale));
                var local = cell.Mesh.GetInstanceTransform(k).Origin;
                cell.Mesh.SetInstanceTransform(k, new Transform3D(basis, local));
                cut++;
            }
        }
        return cut;
    }

    /// <summary>Takes the crop artifacts whose kind starts with <paramref name="kindPrefix"/> within <paramref name="radius"/> of <paramref name="at"/> (game x, z) out of the field (a burnt bale). True when one was there.</summary>
    public bool CropHideArtifact(Vector3 at, float radius, string kindPrefix = "art_hay_bale")
    {
        var found = false;
        foreach (var cell in _cropCells)
        {
            if (cell.Plant || !cell.Kind.StartsWith(kindPrefix, StringComparison.Ordinal) || !GodotObject.IsInstanceValid(cell.Mesh)) continue;
            for (var k = 0; k < cell.Rows.Count; k++)
            {
                if (cell.Cut[k]) continue;
                var r = cell.Rows[k];
                if (new Vector2(r.X - at.X, -r.Y - at.Z).Length() > radius) continue;
                cell.Cut[k] = true;
                cell.Mesh.SetInstanceTransform(k, new Transform3D(Basis.Identity.Scaled(Vector3.One * 0.001f), cell.Mesh.GetInstanceTransform(k).Origin));
                found = true;
            }
        }
        return found;
    }

    static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var l2 = ab.LengthSquared();
        var t = l2 < 1e-9f ? 0f : Mathf.Clamp((p - a).Dot(ab) / l2, 0f, 1f);
        return p.DistanceTo(a + ab * t);
    }
}

using System.Text.Json;

namespace Drycastle.ShooterCore;

/// <summary>The kinds of ground object in ground_objects.json (fallen log, stump, boulder, upturned root disc, ant mound, burrow mound).</summary>
public enum GroundKind : byte { Log = 0, Stump = 1, Boulder = 2, RootPlate = 3, Anthill = 4, Burrow = 5 }

/// <summary>
/// One ground object (map metres, x east, y north, z up; Z is the ground height at its base point). <c>Rot</c> is the yaw: u = (cos, sin).
/// A log lies along u, its midpoint at (X, Y), <c>Len</c> metres long (the only kind that has one), thick end at -u. A root plate's flat faces
/// are across u. A burrow's hole faces u. <c>Scale</c> multiplies the model numbers of its kind (a log's scale is its thickness only).
/// </summary>
public readonly record struct GroundObject(GroundKind Kind, float X, float Y, float Z, float Rot, float Scale, float Len, int ChunkX, int ChunkY);

/// <summary>
/// The model numbers of ground_objects.json (rules <c>props.ground_objects.model</c>, metres at scale 1): the same ones the drawn
/// meshes (pipeline <c>propmeshes.ground_object_mesh</c>) are built from.
/// </summary>
public sealed record GroundModel(
    float LogDiameterM = 0.45f, float LogTopRatio = 0.8f,
    float StumpWidthM = 0.6f, float StumpHeightM = 0.6f, float StumpTopRatio = 0.85f,
    float BoulderWidthM = 1.0f, float BoulderHeightM = 0.7f,
    float RootRadiusM = 1.1f, float RootThicknessM = 0.4f, float RootLiftM = 0.9f,
    float AnthillWidthM = 1.2f, float AnthillHeightM = 0.8f,
    float BurrowWidthM = 1.3f, float BurrowHeightM = 0.4f);

/// <summary>
/// Collision solids of the forest's ground objects (ground_objects.json), pure like <see cref="TreeColliders"/>: logs and stumps are boxes
/// a player can stand on, a boulder a box a little smaller than the rock, a root plate a thin box, an ant mound and a burrow mound a frustum
/// (a square taper) inside the cone. All are solid for movement and shots
/// (<see cref="CollisionLayers.World"/>). Every box lies inside the drawn mesh (the tests check it against the pipeline's meshes), so a
/// player never meets an invisible wall, and the foot is not below the ground, where nothing can be reached.
/// </summary>
public static class GroundObjectColliders
{
    /// <summary>Half the height of a regular hexagon of circumradius 1 (flat top and bottom): the log's section.</summary>
    const float HexHalfHeight = 0.8660254f;
    /// <summary>The log's bottom is this fraction of its thick-end radius under the ground (both ends rest on one level).</summary>
    const float LogSink = 0.07f;

    /// <summary>Parses ground_objects.json (format 1). Unknown kinds are skipped; empty when there are none.</summary>
    public static List<GroundObject> Parse(string json, out GroundModel model)
    {
        model = new GroundModel();
        var list = new List<GroundObject>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("model", out var m) && m.TryGetProperty("kinds", out var k)) model = ParseModel(k);
        if (!root.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Array) return list;
        foreach (var o in objects.EnumerateArray())
        {
            var kind = o.TryGetProperty("kind", out var ks) ? ks.GetString() switch
            {
                "log" => (GroundKind?)GroundKind.Log,
                "stump" => GroundKind.Stump,
                "boulder" => GroundKind.Boulder,
                "rootplate" => GroundKind.RootPlate,
                "anthill" => GroundKind.Anthill,
                "burrow" => GroundKind.Burrow,
                _ => null,
            } : null;
            if (kind is not { } kd) continue;
            var (cx, cy) = o.TryGetProperty("chunk", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() == 2
                ? (c[0].GetInt32(), c[1].GetInt32()) : (0, 0);
            list.Add(new GroundObject(kd, F(o, "x", 0), F(o, "y", 0), F(o, "z", 0), F(o, "rot", 0), F(o, "scale", 1), F(o, "len", 0), cx, cy));
        }
        return list;
    }

    static GroundModel ParseModel(JsonElement kinds)
    {
        var d = new GroundModel();
        float G(string kind, string key, float fallback) => kinds.TryGetProperty(kind, out var e) ? F(e, key, fallback) : fallback;
        return new GroundModel(
            G("log", "diameter_m", d.LogDiameterM), G("log", "top_ratio", d.LogTopRatio),
            G("stump", "width_m", d.StumpWidthM), G("stump", "height_m", d.StumpHeightM), G("stump", "top_ratio", d.StumpTopRatio),
            G("boulder", "width_m", d.BoulderWidthM), G("boulder", "height_m", d.BoulderHeightM),
            G("rootplate", "radius_m", d.RootRadiusM), G("rootplate", "thickness_m", d.RootThicknessM), G("rootplate", "lift_m", d.RootLiftM),
            G("anthill", "width_m", d.AnthillWidthM), G("anthill", "height_m", d.AnthillHeightM),
            G("burrow", "width_m", d.BurrowWidthM), G("burrow", "height_m", d.BurrowHeightM));
    }

    static float F(JsonElement e, string name, float fallback) =>
        e.TryGetProperty(name, out var v) && v.TryGetSingle(out var f) ? f : fallback;

    /// <summary>The surface group of a hit on the object ("wood" for the timber kinds, "rock" for a boulder, "ground" for the earth mounds; the impact classifier treats an unknown group as ground).</summary>
    public static string Surface(GroundKind kind) => kind switch { GroundKind.Boulder => "rock", GroundKind.Anthill or GroundKind.Burrow => "ground", _ => "wood" };

    /// <summary>The solid of one object (always a single box, solid for players and shots), built with the arithmetic of the drawn mesh.</summary>
    public static List<SolidPart> Parts(GroundObject o, GroundModel m)
    {
        var s = o.Scale;
        switch (o.Kind)
        {
            case GroundKind.Log:
            {
                // along the heading, as long as the log less a hand at the ends; as wide and as high as the thin end, so no corner is outside
                var r0 = m.LogDiameterM * s / 2;
                var top = (-LogSink + 2 * HexHalfHeight * m.LogTopRatio) * r0; // height of the thin end's flat top above the ground
                return [new(PartRole.Trunk, PartShape.Box, o.X, o.Y, o.Z - 0.1f, o.Z + 0.97f * top, MathF.Max(0.1f, o.Len / 2 - 0.05f), 0.9f * r0 * m.LogTopRatio, o.Rot)];
            }
            case GroundKind.Stump:
            {
                var half = 0.27f * m.StumpWidthM * s; // the hexagon's section at the top is 0.37 of the width in half-size
                return [new(PartRole.Trunk, PartShape.Box, o.X, o.Y, o.Z, o.Z + m.StumpHeightM * s, half, half, o.Rot)];
            }
            case GroundKind.Boulder:
            {
                var size = m.BoulderWidthM * s;
                // inside the rock's belly (its plan is 0.75-1 as wide as long, rings jittered by up to 15 %), and flat on top at 55 % of its height
                return [new(PartRole.Trunk, PartShape.Box, o.X, o.Y, o.Z, o.Z + 0.55f * m.BoulderHeightM * s, 0.28f * size, 0.22f * size, o.Rot)];
            }
            case GroundKind.Anthill:
            {
                // inside the cone (pipeline propmeshes.anthill_mesh): its octagon is at least 0.68 of the radius wide at the ground, 0.42 at the middle ring (0.55 of the height)
                var r = m.AnthillWidthM * s / 2;
                return [new(PartRole.Trunk, PartShape.Frustum, o.X, o.Y, o.Z, o.Z + 0.55f * m.AnthillHeightM * s, 0.45f * r, 0.45f * r, o.Rot, 0.27f * r)];
            }
            case GroundKind.Burrow:
            {
                // inside the mound (propmeshes.burrow_mesh): at least 0.8 of the radius wide at the ground, 0.57 at the shoulder (0.7 of the height)
                var r = m.BurrowWidthM * s / 2;
                return [new(PartRole.Trunk, PartShape.Frustum, o.X, o.Y, o.Z, o.Z + 0.7f * m.BurrowHeightM * s, 0.5f * r, 0.5f * r, o.Rot, 0.36f * r)];
            }
            default:
            {
                var r = m.RootRadiusM * s;
                // the slab's octagon is 0.78 of its radius at least (rim jitter), its flat faces across the heading
                return [new(PartRole.Trunk, PartShape.Box, o.X, o.Y, o.Z, o.Z + m.RootLiftM * s + 0.75f * r, 0.95f * m.RootThicknessM * s / 2, 0.75f * r, o.Rot)];
            }
        }
    }
}

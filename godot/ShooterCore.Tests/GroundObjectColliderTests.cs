using System.Numerics;
using System.Text.Json;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// Ground objects (#287): the collision boxes built from ground_objects.json's model block lie inside the drawn meshes and fill most of
/// them. The fixture fixtures/ground_object_parity.json is written by pipeline/tests/test_ground_objects.py from
/// <c>propmeshes.ground_object_mesh</c>: per sample object its record and the bounding box of its drawn triangles (map metres).
/// </summary>
public class GroundObjectColliderTests
{
    static JsonElement Golden()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "ground_object_parity.json"));
        return JsonDocument.Parse(json).RootElement;
    }

    /// <summary>The golden samples written as a ground_objects.json (the shape the export writes it in).</summary>
    static string ObjectsJson(JsonElement golden)
    {
        var objects = golden.GetProperty("objects").EnumerateArray().Select(g => g.GetProperty("object").GetRawText());
        return $"{{\"format_version\":1,\"model\":{{\"base_uv2\":true,\"kinds\":{golden.GetProperty("model").GetRawText()}}},\"objects\":[{string.Join(",", objects)}]}}";
    }

    static Vector3 V(JsonElement p) => new(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle());

    [Fact]
    public void EveryBoxLiesInsideTheDrawnMeshAndFillsMostOfIt()
    {
        var golden = Golden();
        var objects = GroundObjectColliders.Parse(ObjectsJson(golden), out var model); // the way the game reads a map
        var samples = golden.GetProperty("objects").EnumerateArray().ToList();
        Assert.Equal(samples.Count, objects.Count);
        for (var i = 0; i < samples.Count; i++)
        {
            var name = samples[i].GetProperty("name").GetString();
            var (lo, hi) = (V(samples[i].GetProperty("bbox")[0]), V(samples[i].GetProperty("bbox")[1]));
            var parts = GroundObjectColliders.Parts(objects[i], model);
            Assert.Single(parts);
            Assert.All(parts, p => Assert.Equal(CollisionLayers.World, p.Layer)); // solid for movement and shots
            var pts = parts[0].Points();
            var (blo, bhi) = (pts.Aggregate(Vector3.Min), pts.Aggregate(Vector3.Max));
            const float Tol = 0.02f;
            Assert.True(blo.X >= lo.X - Tol && blo.Y >= lo.Y - Tol && bhi.X <= hi.X + Tol && bhi.Y <= hi.Y + Tol, $"{name}: box {blo}..{bhi} outside the mesh {lo}..{hi} in plan");
            Assert.True(bhi.Z <= hi.Z + Tol && blo.Z >= objects[i].Z - 0.11f, $"{name}: box heights {blo.Z}..{bhi.Z}, mesh top {hi.Z}");
            // not a token box: it covers at least a third of the mesh's plan extent in x and y and half its height
            Assert.True((bhi.X - blo.X) >= (hi.X - lo.X) / 3 && (bhi.Y - blo.Y) >= (hi.Y - lo.Y) / 3, $"{name}: box {bhi - blo} too small for the mesh {hi - lo}");
            Assert.True(bhi.Z - objects[i].Z >= (hi.Z - objects[i].Z) * 0.5f, $"{name}: box too low");
        }
    }

    [Fact]
    public void LogsAndStumpsAreBoxesAPlayerCanStandOnAndBoulderAndPlateAreSolid()
    {
        var model = new GroundModel();
        var log = new GroundObject(GroundKind.Log, 10, 20, 5, 0.5f, 1.0f, 6, 0, 0);
        var box = GroundObjectColliders.Parts(log, model)[0];
        Assert.Equal((PartRole.Trunk, PartShape.Box), (box.Role, box.Shape));
        Assert.InRange(box.HalfU, 2.9f, 3.0f);               // along the heading, nearly its whole length
        Assert.InRange(box.Z1 - log.Z, 0.25f, 0.4f);         // a 0.45 m log is about 0.37 m high: a jump, not a stride, over the 0.3 m step
        Assert.True(box.Contains(new Vector3(10, 20, 5.2f))); // the log's middle is solid, and has a flat top at Z1 to land on
        Assert.False(box.Contains(new Vector3(10, 20, box.Z1 + 0.05f)));
        // a long way along the heading it is still solid, across it is only as wide as the log
        var u = new Vector3(MathF.Cos(0.5f), MathF.Sin(0.5f), 0);
        Assert.True(box.Contains(new Vector3(10, 20, 5.2f) + u * 2.8f));
        Assert.False(box.Contains(new Vector3(10, 20, 5.2f) + u * 3.2f));
        // thin logs are a step: 0.3 m thick (scale 0.67) tops out under the 0.3 m ledge the controller walks onto
        var thin = GroundObjectColliders.Parts(log with { Scale = 0.3f / 0.45f }, model)[0];
        Assert.True(thin.Z1 - log.Z <= MoveConsts.StepHeight);

        var stump = GroundObjectColliders.Parts(new GroundObject(GroundKind.Stump, 0, 0, 0, 0, 1f, 0, 0, 0), model)[0];
        Assert.Equal(0.6f, stump.Z1, 3);
        Assert.InRange(stump.HalfU, 0.1f, 0.2f);

        var rock = new GroundObject(GroundKind.Boulder, 0, 0, 0, 0, 2.2f, 0, 0, 0);
        var rockBox = GroundObjectColliders.Parts(rock, model)[0];
        Assert.True(rockBox.HalfU < 0.5f * 2.2f * model.BoulderWidthM && rockBox.HalfV < rockBox.HalfU); // a little smaller than the rock
        Assert.Equal("rock", GroundObjectColliders.Surface(GroundKind.Boulder));
        Assert.Equal("wood", GroundObjectColliders.Surface(GroundKind.Log));

        var plate = GroundObjectColliders.Parts(new GroundObject(GroundKind.RootPlate, 0, 0, 0, 0, 1f, 0, 0, 0), model)[0];
        Assert.True(plate.HalfU < 0.25f && plate.HalfV > 0.7f);              // thin across the heading, a metre wide
        Assert.InRange(plate.Z1, 1.5f, 1.9f);                                // about two metres tall at most
        Assert.NotNull(plate.Ray(new Vector3(-5, 0, 1), new Vector3(1, 0, 0), 20)); // a shot along the log meets the plate

        // the earth mounds are tapered solids: wider at the foot than at the top, solid for the world layer
        var hill = GroundObjectColliders.Parts(new GroundObject(GroundKind.Anthill, 0, 0, 0, 0, 1f, 0, 0, 0), model)[0];
        Assert.Equal(PartShape.Frustum, hill.Shape);
        Assert.True(hill.HalfTop < hill.HalfU && hill.HalfU < 0.5f * model.AnthillWidthM);
        Assert.InRange(hill.Z1, 0.4f, 0.5f);                  // 0.55 of the 0.8 m cone
        Assert.True(hill.Contains(new Vector3(0, 0, 0.2f)) && !hill.Contains(new Vector3(0.6f, 0, 0.1f)));
        var burrow = GroundObjectColliders.Parts(new GroundObject(GroundKind.Burrow, 0, 0, 0, 0, 1f, 0, 0, 0), model)[0];
        Assert.Equal(PartShape.Frustum, burrow.Shape);
        Assert.InRange(burrow.Z1, 0.25f, 0.3f);               // a step the player walks onto, not a wall
        Assert.Equal("ground", GroundObjectColliders.Surface(GroundKind.Anthill));
        Assert.Equal("ground", GroundObjectColliders.Surface(GroundKind.Burrow));
    }

    [Fact]
    public void ParseReadsTheContractShapeAndSkipsUnknownKinds()
    {
        const string json = """
            {"format_version":1,"axes":"map metres","chunk_size_m":128.0,
             "model":{"base_uv2":true,"kinds":{"log":{"diameter_m":0.5,"top_ratio":0.7},"stump":{"width_m":0.6,"height_m":0.6,"top_ratio":0.85},
               "boulder":{"width_m":1.0,"height_m":0.7},"rootplate":{"radius_m":1.1,"thickness_m":0.4,"lift_m":0.9},
               "anthill":{"width_m":1.4,"height_m":0.9},"burrow":{"width_m":1.3,"height_m":0.4}}},
             "objects":[{"kind":"log","x":1.5,"y":-2.5,"z":3.0,"rot":0.7,"scale":1.2,"len":6.5,"chunk":[0,-1]},
                        {"kind":"mushroom","x":0,"y":0,"z":0,"rot":0,"scale":1,"chunk":[0,0]},
                        {"kind":"rootplate","x":4,"y":5,"z":6,"rot":1,"scale":0.9,"chunk":[2,3]},
                        {"kind":"anthill","x":7,"y":8,"z":1,"rot":0,"scale":1.1,"chunk":[0,0]}]}
            """;
        var objects = GroundObjectColliders.Parse(json, out var model);
        Assert.Equal(0.5f, model.LogDiameterM);
        Assert.Equal(0.7f, model.LogTopRatio);
        Assert.Equal((1.4f, 0.9f, 0.4f), (model.AnthillWidthM, model.AnthillHeightM, model.BurrowHeightM));
        Assert.Equal(3, objects.Count);
        Assert.Equal(new GroundObject(GroundKind.Log, 1.5f, -2.5f, 3.0f, 0.7f, 1.2f, 6.5f, 0, -1), objects[0]);
        Assert.Equal((GroundKind.RootPlate, 2, 3), (objects[1].Kind, objects[1].ChunkX, objects[1].ChunkY));
        Assert.Equal(GroundKind.Anthill, objects[2].Kind);
        Assert.Empty(GroundObjectColliders.Parse("", out _));
    }
}

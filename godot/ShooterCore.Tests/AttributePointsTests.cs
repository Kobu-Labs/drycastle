using System.Numerics;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The baked candidate points of the runtime attributes (attribute_points.json, #263) and the server's random pick from them.</summary>
public class AttributePointsTests
{
    // as the pipeline writes it: local metres, x east, y north, z the terrain height
    const string Json = """
    {
      "version": 1,
      "axes": "points: local metres, x east, y north (as parcels.json), z the terrain height (as terrain.json)",
      "attributes": [
        { "attribute": "event_spawn", "value": "bigfoot", "areas": ["A-0003", "L-0001"], "area_m2": 52000.5,
          "points": [[10.0, 20.0, 101.5], [30.0, -40.0, 102.0], [-5.5, 7.25, 99.125]] },
        { "attribute": "event_spawn", "value": "yeti", "areas": [], "area_m2": 0.0, "points": [] }
      ]
    }
    """;

    [Fact]
    public void Points_are_converted_to_Godot_ground_axes()
    {
        var points = AttributePoints.Parse(Json).Points("event_spawn", "bigfoot");
        Assert.Equal(3, points.Count);
        Assert.Equal(new Vector3(10f, 101.5f, -20f), points[0]); // north is -z, the height is y
        Assert.Equal(new Vector3(30f, 102f, 40f), points[1]);
        Assert.Equal(new Vector3(-5.5f, 99.125f, -7.25f), points[2]);
    }

    [Fact]
    public void Pick_returns_a_point_of_the_pool_and_follows_the_generator()
    {
        var pool = AttributePoints.Parse(Json);
        var all = pool.Points("event_spawn", "bigfoot").ToHashSet();
        var a = new Random(42);
        var b = new Random(42);
        for (var i = 0; i < 50; i++)
        {
            var p = pool.PickEventSpawn("bigfoot", a);
            Assert.NotNull(p);
            Assert.Contains(p!.Value, all);
            Assert.Equal(p, pool.Pick("event_spawn", "bigfoot", b)); // the same generator state, the same point
        }
    }

    [Fact]
    public void Pick_reaches_every_point_of_the_pool()
    {
        var pool = AttributePoints.Parse(Json);
        var rng = new Random(1);
        var seen = new HashSet<Vector3>();
        for (var i = 0; i < 200; i++) seen.Add(pool.PickEventSpawn("bigfoot", rng)!.Value);
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void An_attribute_with_no_points_yields_no_point()
    {
        var pool = AttributePoints.Parse(Json);
        var rng = new Random(1);
        Assert.Null(pool.PickEventSpawn("yeti", rng)); // listed, but nothing usable was left of its areas
        Assert.Null(pool.PickEventSpawn("nothing", rng)); // not listed: no area carries it
        Assert.Null(pool.Pick("spawns", "bigfoot", rng)); // another attribute: it is not exported
        Assert.Equal(0, pool.Count("event_spawn", "yeti"));
        Assert.Equal(3, pool.Count("event_spawn", "bigfoot"));
    }

    [Fact]
    public void A_map_without_the_file_has_no_points_and_no_error()
    {
        var rng = new Random(1);
        Assert.Null(AttributePoints.FromJson("").PickEventSpawn("bigfoot", rng));
        Assert.Null(AttributePoints.FromJson(null).PickEventSpawn("bigfoot", rng));
        Assert.Null(AttributePoints.Empty.PickEventSpawn("bigfoot", rng));
        Assert.Empty(AttributePoints.Empty.Points("event_spawn", "bigfoot"));
    }

    [Fact]
    public void Unknown_fields_are_ignored_and_a_newer_version_or_broken_file_is_refused()
    {
        var withExtras = Json.Replace("\"version\": 1,", "\"version\": 1, \"something_new\": {\"a\": 1},");
        Assert.Equal(3, AttributePoints.Parse(withExtras).Count("event_spawn", "bigfoot"));

        Assert.Throws<DataFileException>(() => AttributePoints.Parse(Json.Replace("\"version\": 1,", "\"version\": 2,")));
        Assert.Throws<DataFileException>(() => AttributePoints.Parse(Json.Replace("\"version\": 1,", "")));
        Assert.Throws<DataFileException>(() => AttributePoints.Parse("not json"));
        Assert.Throws<DataFileException>(() => AttributePoints.Parse("[]"));
        Assert.Throws<DataFileException>(() => AttributePoints.Parse("""{ "version": 1 }"""));
        Assert.Throws<DataFileException>(() => AttributePoints.Parse(Json.Replace("[10.0, 20.0, 101.5]", "[10.0, 20.0]"))); // a point needs x, y, z
        Assert.Throws<DataFileException>(() => AttributePoints.Parse(Json.Replace("\"value\": \"yeti\"", "\"value\": \"bigfoot\""))); // listed twice
        Assert.Throws<DataFileException>(() => AttributePoints.Parse(Json.Replace("\"attribute\": \"event_spawn\", \"value\": \"yeti\"", "\"value\": \"yeti\"")));
    }

    [Fact]
    public void A_broken_file_is_reported_and_counts_as_no_points()
    {
        var messages = new List<string>();
        var pool = AttributePoints.FromJsonOrEmpty("""{ "version": 9, "attributes": [] }""", messages.Add);
        Assert.Single(messages);
        Assert.Contains("attribute_points.json", messages[0]);
        Assert.Null(pool.PickEventSpawn("bigfoot", new Random(1)));
        AttributePoints.FromJsonOrEmpty(Json, messages.Add);
        Assert.Single(messages); // a good file reports nothing
    }
}

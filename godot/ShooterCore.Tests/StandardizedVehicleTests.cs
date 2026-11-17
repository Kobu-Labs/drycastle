using System.Numerics;
using System.Text.Json;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// A Synty model brought in by scripts/standardize_vehicle.py (docs/standards/car-design.md part B): the model-owned keys of its vehicles.json entry are the numbers the script
/// measured on the (private) model, which the committed facts file docs/research/vehicle-facts.d/&lt;id&gt;.json stands for. The entry is generated (the script patches it), never hand-edited:
/// this fails when they drift. Skins are the five Synty body colours of the car, picked by the colour index of the vehicle record.
/// </summary>
public class StandardizedVehicleTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));

    static IEnumerable<string> FactsFiles()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "docs", "research", "vehicle-facts.d"))) dir = Path.GetDirectoryName(dir);
        Assert.True(dir != null, "docs/research/vehicle-facts.d not found above the test directory");
        return Directory.GetFiles(Path.Combine(dir!, "docs", "research", "vehicle-facts.d"), "*.json");
    }

    public static IEnumerable<object[]> Files() => FactsFiles().Select(f => new object[] { Path.GetFileName(f) });

    static (JsonElement facts, VehicleDef def) Load(string file)
    {
        var path = FactsFiles().Single(f => Path.GetFileName(f) == file);
        var facts = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        var model = facts.GetProperty("model").GetString();
        return (facts, Shipped.Vehicles.Single(v => v.Model == model));
    }

    static Vector3 V(JsonElement a) => new(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());
    static Vector3 V(float[] a) => new(a[0], a[1], a[2]);
    static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.002f) => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    [Theory]
    [MemberData(nameof(Files))]
    public void The_entry_is_what_the_standardizer_measured(string file)
    {
        var (facts, d) = Load(file);
        var box = facts.GetProperty("suggested");
        Assert.Equal(box.GetProperty("half_width").GetSingle(), d.HalfWidth, 3);
        Assert.Equal(box.GetProperty("half_length").GetSingle(), d.HalfLength, 3);
        Assert.Equal(box.GetProperty("height").GetSingle(), d.Height, 3);
        var wheels = facts.GetProperty("wheels");
        // a type without a drive block (the bike and the quad, until #295) has no wheelbase in its entry; the facts still have it
        if (d.Drive != null)
        {
            Assert.Equal(wheels.GetProperty("wheelbase").GetSingle(), d.Drive.Wheelbase, 3);
            Assert.Equal(wheels.GetProperty("track").GetSingle(), d.Drive.Track, 3);
        }

        var seats = facts.GetProperty("seats").EnumerateObject().Select(p => p.Value).ToList();
        Assert.Equal(seats.Count, d.Seats.Count);
        for (var i = 0; i < seats.Count; i++)
        {
            Assert.Equal(seats[i].GetProperty("role").GetString(), d.Seats[i].Role);
            Near(V(seats[i].GetProperty("feet")), V(d.Seats[i].Offset));
            var exposed = seats[i].TryGetProperty("exposed", out var e) && e.GetBoolean();
            Assert.Equal(exposed, d.Seats[i].Exposed); // an open vehicle's rider is exposed (ADR 0012) and has no cabin eye
            if (!exposed) Assert.Equal(seats[i].GetProperty("eye_height").GetSingle(), d.Seats[i].EyeHeight!.Value, 3);
        }

        var panes = facts.GetProperty("glass").EnumerateArray().ToList();
        Assert.Equal(panes.Count, d.Glass.Count);
        for (var i = 0; i < panes.Count; i++)
        {
            Assert.Equal(panes[i].GetProperty("id").GetString(), d.Glass[i].Id);
            Near(V(panes[i].GetProperty("centre")), V(d.Glass[i].Centre));
            Near(V(panes[i].GetProperty("normal")), V(d.Glass[i].Normal));
            var outline = panes[i].GetProperty("outline").EnumerateArray().ToList();
            Assert.Equal(outline.Count, d.Glass[i].Outline!.Length);
            for (var k = 0; k < outline.Count; k++) Assert.True(MathF.Abs(outline[k][0].GetSingle() - d.Glass[i].Outline![k][0]) < 0.002f && MathF.Abs(outline[k][1].GetSingle() - d.Glass[i].Outline![k][1]) < 0.002f);
        }

        var lamps = facts.GetProperty("lamps").EnumerateObject().Select(p => p.Value).ToList();
        foreach (var kind in new[] { "head", "tail" })
        {
            var want = lamps.Where(l => l.GetProperty("kind").GetString() == kind).Select(l => V(l.GetProperty("pos"))).OrderBy(p => p.X).ToList();
            var got = (kind == "head" ? d.Lights!.Head : d.Lights!.Tail).Select(V).OrderBy(p => p.X).ToList();
            Assert.Equal(want.Count, got.Count);
            for (var i = 0; i < want.Count; i++) Near(want[i], got[i]);
        }
        Assert.Equal(1f, d.ModelScale);
        Assert.Equal(0f, d.ModelYawDeg);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_covered_seats_eye_is_under_the_roof_lining_the_model_has_there(string file)
    {
        var (facts, d) = Load(file);
        var seats = facts.GetProperty("seats").EnumerateObject().Select(p => p.Value).ToList();
        for (var i = 0; i < seats.Count; i++)
        {
            if (seats[i].TryGetProperty("exposed", out var open) && open.GetBoolean()) continue; // no roof over a bike's rider
            var eye = d.Seats[i].Offset[1] + d.Seats[i].EyeHeight!.Value;
            var ceiling = seats[i].GetProperty("ceiling_above_eye").GetSingle();
            Assert.True(eye < ceiling - 0.03f, $"seat {i}: eye {eye} against a ceiling at {ceiling}");
            Assert.True(eye <= d.Height);
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Each_skin_is_a_pack_model_of_the_facts_and_the_colour_index_picks_it(string file)
    {
        var (facts, d) = Load(file);
        var skins = facts.GetProperty("skins").EnumerateArray().Select(s => s.GetProperty("model").GetString()!).ToList();
        Assert.Equal(skins, d.Skins);
        Assert.Equal(d.Model, d.Skins[0]);
        Assert.Equal(skins.Count, d.Variants);
        Assert.Equal(skins.Count, d.Skins.Distinct().Count());
        for (var c = 0; c < 2 * skins.Count; c++) Assert.Equal(skins[c % skins.Count], d.SkinModel(c));
        // no tint and no paints on a Synty model (an atlas swap), and the colour byte that picks the skin is 4 bits
        Assert.Null(d.Tint);
        Assert.Empty(d.Paints);
        Assert.InRange(d.Variants, 1, 16);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void A_steering_axis_that_is_tilted_is_a_unit_vector_in_the_vehicles_y_z_plane_and_its_wheel_stands_on_the_ground(string file)
    {
        var (facts, _) = Load(file);
        foreach (var w in facts.GetProperty("wheels").GetProperty("centres").EnumerateObject())
        {
            Assert.True(MathF.Abs(w.Value.GetProperty("centre")[1].GetSingle() - w.Value.GetProperty("radius").GetSingle()) < 0.005f, $"{w.Name}: the axle is a radius up");
            if (!w.Value.TryGetProperty("steer_axis", out var a)) continue;
            Assert.True(w.Value.GetProperty("steer").GetBoolean());
            Assert.Equal(1f, V(a).Length(), 2);
            Assert.Equal(0f, a[0].GetSingle(), 3); // a rake is a tilt about X: the roll stays about X
            Assert.True(a[1].GetSingle() > 0.5f);
        }
    }

    [Fact]
    public void A_type_without_skins_draws_its_model()
    {
        var jeep = Shipped.Vehicles.Single(v => v.Id == "jeep");
        Assert.Empty(jeep.Skins);
        Assert.Equal(jeep.Model, jeep.SkinModel(3));
    }
}

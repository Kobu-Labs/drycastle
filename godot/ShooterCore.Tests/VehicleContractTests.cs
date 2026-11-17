using System.Text.Json;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// The vehicle contract (docs/standards/car-design.md part A, issue "Unify all vehicle models") over EVERY vehicle in vehicles.json, as far as it can be checked
/// from data without Godot. Each rule is one Theory case. A vehicle that fails a rule today is on <see cref="Allowed"/> with a reason; the test fails for a vehicle
/// that fails and is not listed, and for a listed one that now passes (delete the entry: the list only shrinks, the goal is empty).
/// Not checkable here (needs the .glb or code not written yet): node names, wheels, `hull`, interior, seat cover, pane kind, per-seat views (issues 301, 339).
/// </summary>
public class VehicleContractTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));
    // the raw JSON, for "is the block written" (the data model fills a default seats list)
    static readonly Dictionary<string, JsonElement> Raw = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataDir, "vehicles.json"))).RootElement
        .GetProperty("vehicles").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);

    static string? AssetsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "assets", "drycastle-generated-models"))) dir = Path.GetDirectoryName(dir);
        return dir == null ? null : Path.Combine(dir, "assets");
    }

    static bool In(VehicleDef v, float x, float y, float z) => MathF.Abs(x) <= v.HalfWidth + 1e-3f && MathF.Abs(z) <= v.HalfLength + 1e-3f && y >= -1e-3f && y <= v.Height + 1e-3f;

    /// <summary>A rule: null when the vehicle meets it, else what is wrong.</summary>
    delegate string? Rule(VehicleDef v);

    static readonly Dictionary<string, Rule> Rules = new()
    {
        // A: model_scale 1, model_offset 0, model_yaw_deg 0 (a model is authored in the game frame; Synty models are standardized, ours are generated)
        ["default_model_transform"] = v => v.ModelScale == 1 && v.ModelOffset.All(x => x == 0) && v.ModelYawDeg == 0
            ? null : $"model_scale {v.ModelScale}, model_offset [{string.Join(", ", v.ModelOffset)}], model_yaw_deg {v.ModelYawDeg}",
        // A: a drawn model (empty = code-built primitives)
        ["has_model"] = v => v.Model != "" ? null : "no model (code-built)",
        // A: a model file that is not a private pack model must be in the repo
        ["public_model_file_exists"] = v => v.Model == "" || v.Model.StartsWith("pack:") || AssetsDir() is not { } a || File.Exists(Path.Combine(a, v.Model)) ? null : $"{v.Model} is not under assets/",
        // A: every vehicle writes its seats (the data model defaults one driver seat, which hides a missing block). A trailer writes an empty list.
        ["has_seats_block"] = v => Raw[v.Id].TryGetProperty("seats", out _) ? null : "no seats block (the default single driver seat applies)",
        // A: 1..MaxSeats seats, the first the driver; a trailer has none and a trailer block
        ["seat_count"] = v => v.Trailer != null ? (v.Seats.Count == 0 ? null : "a trailer has no seats")
            : v.Seats.Count is >= 1 and <= VehicleSeat.MaxSeats && v.Seats[0].Role == "driver" ? null : $"{v.Seats.Count} seats (1..{VehicleSeat.MaxSeats}, seat 0 the driver)",
        // A: every seat point is inside the hit box
        ["seats_inside_hit_box"] = v => v.Seats.Select((s, i) => (s, i)).Where(t => !In(v, t.s.Offset[0], t.s.Offset[1], t.s.Offset[2])).Select(t => $"seat {t.i} at [{string.Join(", ", t.s.Offset)}]").FirstOrDefault(),
        // A: the eye of a covered seat (the rider's feet + eye_height, or the standing eye) is inside the hit box, so the camera is in the cabin and not above the roof
        ["covered_seat_eye_inside_hit_box"] = v => v.Seats.Select((s, i) => (s, i)).Where(t => !v.SeatExposed(t.i))
            .Select(t => (t.i, y: t.s.Offset[1] + (t.s.EyeHeight ?? MoveConsts.EyeHeight), t.s))
            .Where(t => !In(v, t.s.Offset[0], t.y, t.s.Offset[2])).Select(t => $"seat {t.i} eye y {t.y:F2} (box height {v.Height})").FirstOrDefault(),
        // A: panes inside the hit box (the server tests them in that frame): the centre (as HumveeTests)
        ["glass_inside_hit_box"] = v => v.Glass.Where(g => !In(v, g.Centre[0], g.Centre[1], g.Centre[2])).Select(g => $"pane {g.Id} centre [{string.Join(", ", g.Centre)}]").FirstOrDefault(),
        // A / ADR 0008: a pane is a node with an outline (a rectangle with no outline is a measured paint cell of an old model)
        ["glass_has_outline"] = v => v.Glass.Where(g => g.Outline == null).Select(g => $"pane {g.Id} has no outline").FirstOrDefault(),
        // A: a Synty model is recoloured by an atlas swap, never a tint or paint_material (a tint multiplies the whole atlas)
        ["synty_not_tinted"] = v => !v.Model.StartsWith("pack:") || v.Tint == null && v.PaintMaterial == "" && v.Paints.Count == 0 ? null : "tint/paint_material on a pack model",
    };

    /// <summary>rule -> vehicle id -> why it fails today. Shrinks to empty (issue #325 step 7 and the model swaps).</summary>
    static readonly Dictionary<string, Dictionary<string, string>> Allowed = new()
    {
        ["default_model_transform"] = new()
        {
            ["harvester"] = "Synty farm prefab turned 180 degrees by model_yaw_deg; standardize it (#325 conform)",
        },
        ["covered_seat_eye_inside_hit_box"] = new()
        {
            ["humvee"] = "driver seat has no eye_height (its eye is CockpitRig's, 1.65 m): seat 0 default eye is over the roof; per-seat eye data is #339",
            ["golf_mk2"] = "seats have no eye_height (the driver's eye is CockpitRig's): default eye 2.10 m over a 1.79 m box; per-seat eye data is #339 / conform",
        },
    };

    public static IEnumerable<object[]> RuleNames() => Rules.Keys.Select(k => new object[] { k });

    [Fact]
    public void There_are_vehicles_to_check_and_the_allow_list_names_real_rules_and_vehicles()
    {
        Assert.NotEmpty(Shipped.Vehicles);
        foreach (var (rule, list) in Allowed)
        {
            Assert.True(Rules.ContainsKey(rule), $"allow-list: unknown rule {rule}");
            foreach (var (id, why) in list) { Assert.Contains(Shipped.Vehicles, v => v.Id == id); Assert.False(string.IsNullOrWhiteSpace(why), $"allow-list: {rule}/{id} needs a reason"); }
        }
    }

    [Theory]
    [MemberData(nameof(RuleNames))]
    public void Every_vehicle_meets_the_rule_or_is_on_its_allow_list_and_a_listed_one_still_fails(string rule)
    {
        Allowed.TryGetValue(rule, out var allowed);
        var wrong = new List<string>();
        foreach (var v in Shipped.Vehicles)
        {
            var problem = Rules[rule](v);
            var listed = allowed != null && allowed.ContainsKey(v.Id);
            if (problem != null && !listed) wrong.Add($"{v.Id}: {problem} (fix it, or add it to Allowed[\"{rule}\"] with a reason)");
            if (problem == null && listed) wrong.Add($"{v.Id}: now meets {rule}: remove it from Allowed[\"{rule}\"]");
        }
        Assert.True(wrong.Count == 0, $"{rule}:\n  " + string.Join("\n  ", wrong));
    }
}
